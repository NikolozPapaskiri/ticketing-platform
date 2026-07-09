using System.Net.Http.Json;
using System.Text.Json;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// Thin helpers so tests read as scenarios, not HTTP plumbing. Each test creates its own
/// uniquely-slugged tenant, which is what keeps tests independent on the shared database.
/// </summary>
internal static class ApiClientExtensions
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<TenantDto> CreateTenantAsync(this HttpClient client, string? name = null)
    {
        var slug = $"t-{Guid.NewGuid():N}";
        var response = await client.PostAsJsonAsync("/api/v1/tenants", new { name = name ?? $"Tenant {slug}", slug });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TenantDto>(Json))!;
    }

    public static async Task<EventDto> CreateEventAsync(this HttpClient client, Guid tenantId, string name = "Test Event")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/events");
        request.Headers.Add("X-Tenant-Id", tenantId.ToString());
        request.Content = JsonContent.Create(new { name, startsAt = DateTimeOffset.UtcNow.AddMonths(1) });
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EventDto>(Json))!;
    }

    public static Task<HttpResponseMessage> GetAsTenantAsync(this HttpClient client, Guid tenantId, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Tenant-Id", tenantId.ToString());
        return client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> PostAsTenantAsync(this HttpClient client, Guid tenantId, string url, object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("X-Tenant-Id", tenantId.ToString());
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return client.SendAsync(request);
    }
}

// Local response shapes: tests deserialize only what they assert on.
internal sealed record TenantDto(Guid Id, string Name, string Slug);
internal sealed record EventDto(Guid Id, string Name, string Status, IReadOnlyList<TicketTypeDto> TicketTypes);
internal sealed record TicketTypeDto(Guid Id, string Name, decimal Price, string Currency, int TotalQuantity, int AvailableQuantity);
internal sealed record PageDto<T>(IReadOnlyList<T> Items, int PageNumber, int PageSize, int TotalItems, int TotalPages);
internal sealed record EventListItemDto(Guid Id, string Name, string Status);
internal sealed record ProblemDto(string? Type, string? Title, int? Status, string? Detail, string? TraceId);
