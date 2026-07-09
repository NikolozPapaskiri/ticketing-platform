using System.Net;
using System.Net.Http.Json;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// The authorization matrix and the token lifecycle, end to end: who may do what, and how
/// refresh rotation + reuse detection behave.
/// </summary>
[Collection(nameof(ApiCollection))]
public class AuthTests
{
    private readonly HttpClient _client;

    public AuthTests(TicketingApiFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task RegisterCustomer_ThenLogin_Works()
    {
        var email = $"c-{Guid.NewGuid():N}@test.local";

        var register = await _client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = "Secret123$" });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var user = await register.Content.ReadFromJsonAsync<UserDto>(ApiClientExtensions.Json);
        Assert.Equal("Customer", user!.Role); // self-service is ALWAYS Customer
        Assert.Null(user.TenantId);

        var token = await _client.LoginAsync(email, "Secret123$");
        Assert.False(string.IsNullOrWhiteSpace(token));
    }

    [Fact]
    public async Task Register_DuplicateEmail_Returns409()
    {
        var (email, _, _) = await _client.CreateCustomerAsync();

        var response = await _client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = "Other123$" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401_WithVagueMessage()
    {
        var (email, _, _) = await _client.CreateCustomerAsync();

        var response = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "wrong-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(ApiClientExtensions.Json);
        Assert.Equal("Invalid credentials.", problem!.Detail); // no "wrong password" vs "no user" oracle
    }

    [Fact]
    public async Task Customer_OnStaffAndAdminEndpoints_Returns403()
    {
        var (_, _, customerToken) = await _client.CreateCustomerAsync();

        var events = await _client.GetAsAsync(customerToken, "/api/v1/events");
        var tenants = await _client.PostAsAsync(customerToken, "/api/v1/tenants", new { name = "Hack", slug = "hack" });
        var staff = await _client.PostAsAsync(customerToken, "/api/v1/auth/register-staff",
            new { email = "x@x.local", password = "Xxxxx123$", role = "PlatformAdmin", tenantId = (Guid?)null });

        // Authenticated but not allowed -> 403 (unlike the anonymous 401).
        Assert.Equal(HttpStatusCode.Forbidden, events.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, tenants.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, staff.StatusCode);
    }

    [Fact]
    public async Task Admin_HasNoTenantClaim_SoStaffEndpointsReturn403()
    {
        // The OrganizerStaff policy requires role AND tenant claim; an admin has neither the
        // role nor a tenant, so tenant-scoped endpoints reject the platform admin by design.
        var adminToken = await _client.LoginAsAdminAsync();

        var response = await _client.GetAsAsync(adminToken, "/api/v1/events");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RegisterStaff_UnknownTenant_Returns404()
    {
        var adminToken = await _client.LoginAsAdminAsync();

        var response = await _client.PostAsAsync(adminToken, "/api/v1/auth/register-staff",
            new { email = $"s-{Guid.NewGuid():N}@test.local", password = "Staff123$", role = "OrganizerStaff", tenantId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_RotatesToken_AndDetectsReuse()
    {
        var (email, password, _) = await _client.CreateCustomerAsync();
        var login = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        var pair1 = (await login.Content.ReadFromJsonAsync<AuthDto>(ApiClientExtensions.Json))!;

        // Legitimate refresh: rotates, returns a NEW refresh token.
        var refresh = await _client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = pair1.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var pair2 = (await refresh.Content.ReadFromJsonAsync<AuthDto>(ApiClientExtensions.Json))!;
        Assert.NotEqual(pair1.RefreshToken, pair2.RefreshToken);

        // Replaying the rotated token = theft signal -> 401 AND the whole family is revoked...
        var replay = await _client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = pair1.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // ...including the once-valid newer token: thief and stolen session die together.
        var family = await _client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = pair2.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, family.StatusCode);
    }
}
