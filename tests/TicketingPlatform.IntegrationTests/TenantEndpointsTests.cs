using System.Net;
using System.Net.Http.Json;

namespace TicketingPlatform.IntegrationTests;

[Collection(nameof(ApiCollection))]
public class TenantEndpointsTests
{
    private readonly HttpClient _client;

    public TenantEndpointsTests(TicketingApiFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Create_ReturnsCreatedTenant()
    {
        var slug = $"t-{Guid.NewGuid():N}";

        var response = await _client.PostAsJsonAsync("/api/v1/tenants", new { name = "Acme", slug });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var tenant = await response.Content.ReadFromJsonAsync<TenantDto>(ApiClientExtensions.Json);
        Assert.Equal(slug, tenant!.Slug);
    }

    [Fact]
    public async Task Create_DuplicateSlug_Returns409Problem()
    {
        var tenant = await _client.CreateTenantAsync();

        // The service pre-checks, but the DB unique index is the authoritative guard.
        var response = await _client.PostAsJsonAsync("/api/v1/tenants", new { name = "Copycat", slug = tenant.Slug });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(ApiClientExtensions.Json);
        Assert.Equal("Slug already in use", problem!.Title);
        Assert.Contains(tenant.Slug, problem.Detail);
        Assert.NotNull(problem.TraceId);
    }

    [Fact]
    public async Task Create_InvalidSlug_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/tenants", new { name = "Bad", slug = "Not A Slug!" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_ReturnsCreatedTenants_WithoutTenantHeader()
    {
        // Tenants are the top-level owners: the admin list needs no X-Tenant-Id.
        var created = await _client.CreateTenantAsync();

        var tenants = await _client.GetFromJsonAsync<List<TenantDto>>("/api/v1/tenants", ApiClientExtensions.Json);

        Assert.Contains(tenants!, t => t.Id == created.Id);
    }
}
