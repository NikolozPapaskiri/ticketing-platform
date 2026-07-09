using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TicketingPlatform.Api.Development;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// Test helpers that authenticate the way a real client does: login -> Bearer token. Since
/// Phase 3 the tenant comes from the token's tenant_id claim, so "acting as tenant X" means
/// "holding a staff token for tenant X" - there is no header to spoof anymore.
/// </summary>
internal static class ApiClientExtensions
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<string> LoginAsync(this HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthDto>(Json);
        return auth!.AccessToken;
    }

    /// <summary>The seeded dev PlatformAdmin (DevDataSeeder) is the root of every test scenario.</summary>
    public static Task<string> LoginAsAdminAsync(this HttpClient client) =>
        client.LoginAsync(DevDataSeeder.AdminEmail, DevDataSeeder.AdminPassword);

    public static async Task<TenantDto> CreateTenantAsync(this HttpClient client, string? adminToken = null)
    {
        adminToken ??= await client.LoginAsAdminAsync();
        var slug = $"t-{Guid.NewGuid():N}";
        var response = await client.PostAsAsync(adminToken, "/api/v1/tenants", new { name = $"Tenant {slug}", slug });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TenantDto>(Json))!;
    }

    /// <summary>Provisions staff for the tenant (as admin) and logs them in. Returns their Bearer token.</summary>
    public static async Task<string> CreateStaffAsync(this HttpClient client, Guid tenantId, string? adminToken = null)
    {
        adminToken ??= await client.LoginAsAdminAsync();
        var email = $"staff-{Guid.NewGuid():N}@test.local";
        const string password = "Staff123$";
        var response = await client.PostAsAsync(adminToken, "/api/v1/auth/register-staff",
            new { email, password, role = "OrganizerStaff", tenantId });
        response.EnsureSuccessStatusCode();
        return await client.LoginAsync(email, password);
    }

    /// <summary>One-call arrange for the common case: a tenant plus a logged-in staff member.</summary>
    public static async Task<(TenantDto Tenant, string StaffToken)> CreateTenantWithStaffAsync(this HttpClient client)
    {
        var adminToken = await client.LoginAsAdminAsync();
        var tenant = await client.CreateTenantAsync(adminToken);
        var staffToken = await client.CreateStaffAsync(tenant.Id, adminToken);
        return (tenant, staffToken);
    }

    public static async Task<(string Email, string Password, string Token)> CreateCustomerAsync(this HttpClient client)
    {
        var email = $"cust-{Guid.NewGuid():N}@test.local";
        const string password = "Customer123$";
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password });
        response.EnsureSuccessStatusCode();
        return (email, password, await client.LoginAsync(email, password));
    }

    public static async Task<EventDto> CreateEventAsync(this HttpClient client, string staffToken, string name = "Test Event")
    {
        var response = await client.PostAsAsync(staffToken, "/api/v1/events",
            new { name, startsAt = DateTimeOffset.UtcNow.AddMonths(1) });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EventDto>(Json))!;
    }

    public static Task<HttpResponseMessage> GetAsAsync(this HttpClient client, string token, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> PostAsAsync(this HttpClient client, string token, string url, object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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
internal sealed record AuthDto(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken);
internal sealed record UserDto(Guid Id, string Email, string Role, Guid? TenantId);
