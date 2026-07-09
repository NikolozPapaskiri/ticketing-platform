using System.Net;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// The probes Kubernetes will call. Liveness checks nothing external (a dependency outage must
/// not restart pods); readiness checks Postgres + Redis + RabbitMQ - all real containers here,
/// so Healthy is a genuine end-to-end statement.
/// </summary>
[Collection(nameof(ApiCollection))]
public class HealthTests
{
    private readonly HttpClient _client;

    public HealthTests(TicketingApiFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Live_Returns200_WithoutTouchingDependencies()
    {
        var response = await _client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ready_Returns200_WhenPostgresRedisAndRabbitAreUp()
    {
        var response = await _client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Probes_AreAnonymous()
    {
        // No Bearer token on either call above - Kubernetes does not log in. This test exists
        // so nobody ever "secures" the probes and silently breaks every deployment.
        var live = await _client.GetAsync("/health/live");
        var ready = await _client.GetAsync("/health/ready");

        Assert.NotEqual(HttpStatusCode.Unauthorized, live.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, ready.StatusCode);
    }
}
