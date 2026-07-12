using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Infrastructure.Messaging;
using TicketingPlatform.Infrastructure.Persistence;
using WireMock.Server;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// Boots the real API in-memory against real infrastructure: throwaway PostgreSQL 17, Redis 7,
/// and RabbitMQ containers, plus a WireMock server standing in for the payment provider (so
/// tests can script provider failures deterministically). One set per test run (collection
/// fixture); tests keep themselves independent by creating uniquely-named tenants/users.
/// Unsealed on purpose: ExpiryTests subclasses it with a short hold TTL.
/// </summary>
public class TicketingApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();
    private readonly RedisContainer _redis = new RedisBuilder("redis:7").Build();
    // Explicit credentials: the module's default is NOT guest/guest, and RabbitMQ restricts
    // the literal 'guest' user to loopback (a Docker port-proxy connection does not qualify).
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:3-management")
        .WithUsername("ticketing")
        .WithPassword("ticketing")
        .Build();

    /// <summary>Scriptable payment provider. Tests reset + stub it per scenario.</summary>
    public WireMockServer PaymentProvider { get; } = WireMockServer.Start();
    public string RabbitHost => _rabbit.Hostname;
    public int RabbitPort => _rabbit.GetMappedPublicPort(5672);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Swap external endpoints before the host builds; everything else is production wiring.
        builder.UseSetting("ConnectionStrings:Default", _postgres.GetConnectionString());
        builder.UseSetting("ConnectionStrings:Redis", _redis.GetConnectionString());
        builder.UseSetting("RabbitMq:HostName", _rabbit.Hostname);
        builder.UseSetting("RabbitMq:Port", _rabbit.GetMappedPublicPort(5672).ToString());
        builder.UseSetting("RabbitMq:UserName", "ticketing");
        builder.UseSetting("RabbitMq:Password", "ticketing");
        // Deliberately long so the outbox retry tests can prove a failed publish is scheduled
        // rather than retried by every one-second dispatcher poll.
        builder.UseSetting("RabbitMq:OutboxRetryBaseSeconds", "30");
        builder.UseSetting("RabbitMq:OutboxMaxAttempts", "2");
        builder.UseSetting("RabbitMq:OutboxLockSeconds", "2");
        builder.UseSetting("RabbitMq:ConsumerRetryDelayMilliseconds", "500");
        builder.UseSetting("RabbitMq:ConsumerMaxAttempts", "3");
        builder.UseSetting("PaymentProvider:BaseUrl", PaymentProvider.Urls[0] + "/");
        builder.UseSetting("PaymentProvider:RetryBaseDelayMs", "50"); // fast retry storms in tests
        builder.UseSetting("RateLimiting:AuthRequestsPerMinute", "100000"); // the suite logs in constantly
        builder.UseSetting("WaitingRoom:AdmitBatchSize", "1");   // one admission per tick => positions observable
        builder.UseSetting("WaitingRoom:AdmitIntervalSeconds", "1"); // fast valve so queue tests finish in seconds
        builder.UseSetting("FileStorage:Root", Path.Combine(Path.GetTempPath(), $"ticketing-tests-{Guid.NewGuid():N}"));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IFileStorage>();
            services.AddSingleton<TransientFileStorage>();
            services.AddSingleton<IFileStorage>(sp => sp.GetRequiredService<TransientFileStorage>());
            services.RemoveAll<IOutboxPublisher>();
            services.AddSingleton<FailOnceOutboxPublisher>();
            services.AddSingleton<IOutboxPublisher>(sp => sp.GetRequiredService<FailOnceOutboxPublisher>());
        });
        builder.UseEnvironment("Development");
    }

    async Task IAsyncLifetime.InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync(), _rabbit.StartAsync());

        // Touching Services builds the host, which (Development) migrates + seeds the admin.
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<TicketingDbContext>().Database.MigrateAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        PaymentProvider.Stop();
        await Task.WhenAll(
            _postgres.DisposeAsync().AsTask(),
            _redis.DisposeAsync().AsTask(),
            _rabbit.DisposeAsync().AsTask());
    }
}

[CollectionDefinition(nameof(ApiCollection))]
public sealed class ApiCollection : ICollectionFixture<TicketingApiFactory>;
