using System.Net;
using System.Net.Http.Json;

namespace TicketingPlatform.IntegrationTests;

[Collection(nameof(ApiCollection))]
public class TenantEndpointsTests
{
    private readonly HttpClient _client;

    public TenantEndpointsTests(TicketingApiFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Create_AsAdmin_ReturnsCreatedTenant()
    {
        var adminToken = await _client.LoginAsAdminAsync();
        var slug = $"t-{Guid.NewGuid():N}";

        var response = await _client.PostAsAsync(adminToken, "/api/v1/tenants", new { name = "Acme", slug });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var tenant = await response.Content.ReadFromJsonAsync<TenantDto>(ApiClientExtensions.Json);
        Assert.Equal(slug, tenant!.Slug);
    }

    [Fact]
    public async Task Create_DuplicateSlug_Returns409Problem()
    {
        var adminToken = await _client.LoginAsAdminAsync();
        var tenant = await _client.CreateTenantAsync(adminToken);

        // The service pre-checks, but the DB unique index is the authoritative guard.
        var response = await _client.PostAsAsync(adminToken, "/api/v1/tenants", new { name = "Copycat", slug = tenant.Slug });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(ApiClientExtensions.Json);
        Assert.Equal("Slug already in use", problem!.Title);
        Assert.Contains(tenant.Slug, problem.Detail);
        Assert.NotNull(problem.TraceId);
    }

    [Fact]
    public async Task Create_InvalidSlug_Returns400()
    {
        var adminToken = await _client.LoginAsAdminAsync();

        var response = await _client.PostAsAsync(adminToken, "/api/v1/tenants", new { name = "Bad", slug = "Not A Slug!" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_AsAdmin_ReturnsCreatedTenants()
    {
        var adminToken = await _client.LoginAsAdminAsync();
        var created = await _client.CreateTenantAsync(adminToken);

        var response = await _client.GetAsAsync(adminToken, "/api/v1/tenants");
        var tenants = await response.Content.ReadFromJsonAsync<List<TenantDto>>(ApiClientExtensions.Json);

        Assert.Contains(tenants!, t => t.Id == created.Id);
    }

    [Fact]
    public async Task PublicList_Anonymous_ReturnsCreatedTenants()
    {
        var adminToken = await _client.LoginAsAdminAsync();
        var created = await _client.CreateTenantAsync(adminToken);

        var response = await _client.GetAsync("/api/v1/public/tenants");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tenants = await response.Content.ReadFromJsonAsync<List<TenantDto>>(ApiClientExtensions.Json);
        Assert.Contains(tenants!, t => t.Id == created.Id);
    }

    [Fact]
    public async Task Tenants_WithoutToken_Returns401()
    {
        var response = await _client.GetAsync("/api/v1/tenants");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
