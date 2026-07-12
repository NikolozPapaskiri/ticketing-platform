using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TicketingPlatform.Infrastructure.Messaging;
using TicketingPlatform.Infrastructure.WaitingRoom;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// API profile with a deliberately unreachable broker. Role=Api runs NO background workers, so a
/// dead broker cannot crash startup (nothing dispatches/consumes), which lets these tests also
/// prove that a RabbitMQ outage does not flip API readiness.
/// </summary>
public sealed class ApiRoleApiFactory : TicketingApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Hosting:Role", "Api");
        builder.UseSetting("RabbitMq:HostName", "broker.invalid"); // unreachable on purpose
        builder.UseSetting("RabbitMq:Port", "1");
    }
}

/// <summary>Worker profile against the real broker: runs the background services, serves only health.</summary>
public sealed class WorkerRoleApiFactory : TicketingApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Hosting:Role", "Worker");
    }
}

/// <summary>
/// PR 6 §6.1/§6.2 - the API profile. No polling workers run in an API pod (so scaling HTTP
/// replicas cannot multiply admission valves or scheduled work), it still serves the application
/// surface, and RabbitMQ is an ASYNC dependency: a broker outage buffers the outbox and must not
/// pull the pod from the load balancer.
/// </summary>
public class ApiRoleHostTests : IClassFixture<ApiRoleApiFactory>
{
    private readonly ApiRoleApiFactory _factory;
    public ApiRoleHostTests(ApiRoleApiFactory factory) => _factory = factory;

    [Fact]
    public void ApiRole_RegistersNoBackgroundWorkers()
    {
        var hosted = _factory.Services.GetServices<IHostedService>().Select(s => s.GetType()).ToList();

        Assert.DoesNotContain(typeof(OutboxDispatcher), hosted);
        Assert.DoesNotContain(typeof(NotificationConsumer), hosted);
        Assert.DoesNotContain(typeof(HoldExpiryService), hosted);
        Assert.DoesNotContain(typeof(PaymentReconciliationService), hosted);
        Assert.DoesNotContain(typeof(AvailabilityProjectionConsumer), hosted);
        Assert.DoesNotContain(typeof(TicketIssuerConsumer), hosted);
        Assert.DoesNotContain(typeof(WaitingRoomAdmitter), hosted);
        Assert.DoesNotContain(typeof(RabbitMqTopologyInitializer), hosted);
    }

    [Fact]
    public void ApiRole_ReadinessDoesNotGateOnRabbitMq()
    {
        var registrations = _factory.Services
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .ToDictionary(r => r.Name, r => r.Tags);

        Assert.Contains("ready", registrations["postgres"]);
        Assert.Contains("ready", registrations["redis"]);
        Assert.DoesNotContain("ready", registrations["rabbitmq"]); // async: reported, not gating
    }

    [Fact]
    public async Task ApiRole_StaysReadyWhenBrokerUnreachable()
    {
        var client = _factory.CreateClient();

        // Postgres + Redis are up; RabbitMQ is unreachable but not part of the API's readiness set.
        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        // The detailed view still surfaces the broker as unhealthy (observability != gating).
        var detail = await client.GetAsync("/health/detail");
        var body = await detail.Content.ReadAsStringAsync();
        Assert.Contains("rabbitmq", body);
        Assert.Contains("Unhealthy", body);
    }

    [Fact]
    public async Task ApiRole_ServesTheApplicationSurface()
    {
        var client = _factory.CreateClient();

        // A controller route exists (401 because it needs auth - the point is it is MAPPED).
        var events = await client.GetAsync("/api/v1/events");
        Assert.Equal(HttpStatusCode.Unauthorized, events.StatusCode);
    }
}

/// <summary>
/// PR 6 §6.1/§6.2 - the worker profile. It runs the background services, gates readiness on the
/// broker it depends on, and serves only health probes (no application surface).
/// </summary>
public class WorkerRoleHostTests : IClassFixture<WorkerRoleApiFactory>
{
    private readonly WorkerRoleApiFactory _factory;
    public WorkerRoleHostTests(WorkerRoleApiFactory factory) => _factory = factory;

    [Fact]
    public void WorkerRole_RegistersBackgroundWorkers()
    {
        var hosted = _factory.Services.GetServices<IHostedService>().Select(s => s.GetType()).ToList();

        Assert.Contains(typeof(OutboxDispatcher), hosted);
        Assert.Contains(typeof(WaitingRoomAdmitter), hosted);
        Assert.Contains(typeof(TicketIssuerConsumer), hosted);
        Assert.Contains(typeof(RabbitMqTopologyInitializer), hosted);
    }

    [Fact]
    public void WorkerRole_ReadinessGatesOnRabbitMq()
    {
        var registrations = _factory.Services
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .ToDictionary(r => r.Name, r => r.Tags);

        Assert.Contains("ready", registrations["rabbitmq"]); // a worker cannot function without it
    }

    [Fact]
    public async Task WorkerRole_ServesHealthButNotTheApplicationSurface()
    {
        var client = _factory.CreateClient();

        var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);

        // No controllers are mapped on a worker: the application surface simply is not there.
        var events = await client.GetAsync("/api/v1/events");
        Assert.Equal(HttpStatusCode.NotFound, events.StatusCode);
    }
}
