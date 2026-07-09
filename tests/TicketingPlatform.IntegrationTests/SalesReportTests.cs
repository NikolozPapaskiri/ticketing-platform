using System.Net;
using System.Net.Http.Json;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace TicketingPlatform.IntegrationTests;

/// <summary>The vertical-slice feature, tested exactly like every layered endpoint.</summary>
[Collection(nameof(ApiCollection))]
public class SalesReportTests
{
    private readonly TicketingApiFactory _factory;
    private readonly HttpClient _client;

    public SalesReportTests(TicketingApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task SalesReport_AggregatesConfirmedOrders()
    {
        _factory.PaymentProvider.Reset();
        _factory.PaymentProvider
            .Given(Request.Create().WithPath("/charges").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { chargeId = "ch_report" }));

        var (_, staff) = await _client.CreateTenantWithStaffAsync();
        var ev = await _client.CreateEventAsync(staff);
        var ttResponse = await _client.PostAsAsync(staff, $"/api/v1/events/{ev.Id}/ticket-types",
            new { name = "GA", price = 25m, currency = "USD", totalQuantity = 100 });
        var tt = (await ttResponse.Content.ReadFromJsonAsync<TicketTypeDto>(ApiClientExtensions.Json))!;

        // Two confirmed orders (2 + 3 tickets at 25) and one hold left unpurchased.
        foreach (var quantity in new[] { 2, 3 })
        {
            var holdResponse = await _client.PostAsAsync(staff, "/api/v1/holds", new { ticketTypeId = tt.Id, quantity });
            var hold = await holdResponse.Content.ReadFromJsonAsync<HoldDto>(ApiClientExtensions.Json);
            await _client.PostAsAsync(staff, "/api/v1/orders", new { holdId = hold!.Id, customerEmail = "r@example.com" });
        }
        await _client.PostAsAsync(staff, "/api/v1/holds", new { ticketTypeId = tt.Id, quantity = 4 }); // no order

        var response = await _client.GetAsAsync(staff, $"/api/v1/events/{ev.Id}/sales-report");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<ReportDto>(ApiClientExtensions.Json);

        Assert.Equal(5, report!.TotalTicketsSold);       // holds without orders are not sales
        Assert.Equal(125m, report.TotalRevenue);         // 2*25 + 3*25
        var line = Assert.Single(report.Lines);
        Assert.Equal("GA", line.TicketTypeName);
    }

    [Fact]
    public async Task SalesReport_IsTenantScoped_AndRequiresAuth()
    {
        var (_, staffA) = await _client.CreateTenantWithStaffAsync();
        var (_, staffB) = await _client.CreateTenantWithStaffAsync();
        var ev = await _client.CreateEventAsync(staffA);

        var anonymous = await _client.GetAsync($"/api/v1/events/{ev.Id}/sales-report");
        var foreign = await _client.GetAsAsync(staffB, $"/api/v1/events/{ev.Id}/sales-report");
        var owner = await _client.GetAsAsync(staffA, $"/api/v1/events/{ev.Id}/sales-report");

        // The slice bypasses the layers, NOT the security model: same 401/404/200 matrix.
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
    }

    private sealed record ReportDto(Guid EventId, string EventName, int TotalTicketsSold, decimal TotalRevenue, List<LineDto> Lines);
    private sealed record LineDto(string TicketTypeName, int TicketsSold, decimal Revenue);
}
