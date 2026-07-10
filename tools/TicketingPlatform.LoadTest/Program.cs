// Flash-sale load harness: many concurrent buyers hammer POST /holds for ONE ticket type with
// limited inventory - the exact hot-row contention the three IReservationStrategy
// implementations exist for. Run it once per "Reservation:Strategy" value and compare.
//
//   dotnet run -c Release --project tools/TicketingPlatform.LoadTest -- \
//     --label Optimistic [--base http://localhost:5000] [--workers 100] [--capacity 300]
//
// The harness provisions its own tenant/staff/event/ticket-type through the public API (same
// setup path as the integration tests), fires the storm, then verifies from the API's own
// event graph that EXACTLY capacity tickets were sold: successes == capacity - remaining,
// and never more than capacity. Output ends with a markdown row for docs/LOAD_TEST.md.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

var baseUrl = Arg("--base") ?? "http://localhost:5000";
var workers = int.Parse(Arg("--workers") ?? "100");
var capacity = int.Parse(Arg("--capacity") ?? "300");
var label = Arg("--label") ?? "unlabeled";

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };

// --- Setup: admin -> tenant -> staff -> published event with one contended ticket type.
var admin = await LoginAsync("admin@platform.local", "Admin123$");
var slug = $"lt-{Guid.NewGuid():N}";
var tenant = await PostAsync<JsonElement>(admin, "/api/v1/tenants", new { name = $"LoadTest {slug}", slug });
var staffEmail = $"lt-{Guid.NewGuid():N}@test.local";
await PostAsync<JsonElement>(admin, "/api/v1/auth/register-staff",
    new { email = staffEmail, password = "Load123$", role = "OrganizerStaff", tenantId = tenant.GetProperty("id").GetGuid() });
var staff = await LoginAsync(staffEmail, "Load123$");
var ev = await PostAsync<JsonElement>(staff, "/api/v1/events",
    new { name = "Flash Sale", startsAt = DateTimeOffset.UtcNow.AddMonths(1), category = "Concert" });
var eventId = ev.GetProperty("id").GetGuid();
await PostAsync<JsonElement?>(staff, $"/api/v1/events/{eventId}/publish", null);
var tt = await PostAsync<JsonElement>(staff, $"/api/v1/events/{eventId}/ticket-types",
    new { name = "GA", price = 25m, currency = "USD", totalQuantity = capacity });
var ticketTypeId = tt.GetProperty("id").GetGuid();

Console.WriteLine($"strategy={label} workers={workers} capacity={capacity} event={eventId}");

// --- The storm. Every worker loops "reserve 1 ticket" until the harness calls it: either the
// stock is provably gone or nothing has succeeded for a while (sold out / retry livelock).
var successes = 0; var conflicts = 0; var failures = 0;
var latenciesMs = new List<double>[workers];
var successLatenciesMs = new List<double>[workers];
long lastSuccessTicks = Stopwatch.GetTimestamp();
using var stop = new CancellationTokenSource();
var holdBody = JsonSerializer.Serialize(new { ticketTypeId, quantity = 1 }, json);
var wall = Stopwatch.StartNew();

var tasks = Enumerable.Range(0, workers).Select(w => Task.Run(async () =>
{
    var mine = latenciesMs[w] = new List<double>();
    var mineOk = successLatenciesMs[w] = new List<double>();
    while (!stop.IsCancellationRequested)
    {
        var sw = Stopwatch.StartNew();
        HttpStatusCode status;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/holds")
            {
                Content = new StringContent(holdBody, System.Text.Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staff);
            using var response = await http.SendAsync(request, stop.Token);
            status = response.StatusCode;
        }
        catch (OperationCanceledException) { break; }
        sw.Stop();
        mine.Add(sw.Elapsed.TotalMilliseconds);

        if (status == HttpStatusCode.Created)
        {
            mineOk.Add(sw.Elapsed.TotalMilliseconds);
            Interlocked.Exchange(ref lastSuccessTicks, Stopwatch.GetTimestamp());
            if (Interlocked.Increment(ref successes) >= capacity) stop.Cancel();
        }
        else if (status == HttpStatusCode.Conflict)
        {
            Interlocked.Increment(ref conflicts);
            // Sold out or lost the race - keep trying until the monitor says the well is dry.
        }
        else
        {
            Interlocked.Increment(ref failures);
        }
    }
})).ToArray();

// Monitor: end the run when stock is gone or 5s pass without a single successful reservation
// (either sold out with stragglers conflicting, or the strategy has livelocked - both are data).
var monitor = Task.Run(async () =>
{
    while (!stop.IsCancellationRequested)
    {
        await Task.Delay(250);
        var quiet = Stopwatch.GetElapsedTime(Interlocked.Read(ref lastSuccessTicks));
        if (Volatile.Read(ref successes) >= capacity || (quiet > TimeSpan.FromSeconds(5) && wall.Elapsed > TimeSpan.FromSeconds(6)))
            stop.Cancel();
    }
});

await Task.WhenAll(tasks);
wall.Stop();
await monitor;

// --- Verify against live truth (the tenant-scoped event graph, not the eventual read model).
var graphResponse = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/events/{eventId}");
graphResponse.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staff);
var graph = JsonSerializer.Deserialize<JsonElement>(await (await http.SendAsync(graphResponse)).Content.ReadAsStringAsync(), json);
var remaining = graph.GetProperty("ticketTypes")[0].GetProperty("availableQuantity").GetInt32();
var oversold = successes > capacity || successes != capacity - remaining;

var all = latenciesMs.SelectMany(l => l).OrderBy(v => v).ToArray();
var ok = successLatenciesMs.SelectMany(l => l).OrderBy(v => v).ToArray();
double P(double[] xs, double p) => xs.Length == 0 ? 0 : xs[Math.Min(xs.Length - 1, (int)Math.Ceiling(p * xs.Length) - 1)];

var attempts = all.Length;
Console.WriteLine($"attempts={attempts} sold={successes} conflicts={conflicts} failures={failures} remaining={remaining}");
Console.WriteLine($"wall={wall.Elapsed.TotalSeconds:F1}s attemptRps={attempts / wall.Elapsed.TotalSeconds:F0} soldPerSec={successes / wall.Elapsed.TotalSeconds:F0}");
Console.WriteLine($"latency(all) p50={P(all, .50):F0}ms p95={P(all, .95):F0}ms p99={P(all, .99):F0}ms | latency(sold) p50={P(ok, .50):F0}ms p95={P(ok, .95):F0}ms");
Console.WriteLine(oversold
    ? $"!!! OVERSELL/MISMATCH: sold={successes}, capacity={capacity}, remaining={remaining}"
    : $"integrity OK: sold {successes}/{capacity}, remaining {remaining}, no oversell");

// Markdown row for docs/LOAD_TEST.md:
Console.WriteLine($"| {label} | {attempts} | {successes}/{capacity} | {conflicts} | {wall.Elapsed.TotalSeconds:F1}s | {successes / wall.Elapsed.TotalSeconds:F0}/s | {P(all, .50):F0} ms | {P(all, .95):F0} ms | {P(all, .99):F0} ms | {(oversold ? "OVERSOLD" : "0")} |");
return oversold ? 1 : 0;

string? Arg(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

async Task<string> LoginAsync(string email, string password)
{
    var response = await http.PostAsJsonAsync("/api/v1/auth/login", new { email, password }, json);
    response.EnsureSuccessStatusCode();
    return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), json)
        .GetProperty("accessToken").GetString()!;
}

async Task<T> PostAsync<T>(string token, string path, object? body)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, path);
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    if (body is not null) request.Content = JsonContent.Create(body, options: json);
    var response = await http.SendAsync(request);
    if (!response.IsSuccessStatusCode)
        throw new InvalidOperationException($"{path} -> {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    var text = await response.Content.ReadAsStringAsync();
    return text.Length == 0 ? default! : JsonSerializer.Deserialize<T>(text, json)!;
}
