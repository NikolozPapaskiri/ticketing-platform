using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using TicketingPlatform.Infrastructure.Persistence;
using WireMock.Server;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// Boots the real API in-memory against real infrastructure: a throwaway PostgreSQL 17
/// container, a throwaway Redis 7 container, and a WireMock server standing in for the payment
/// provider (so tests can script provider failures - 500s, declines - deterministically).
/// One set per test run (collection fixture); tests keep themselves independent by creating
/// uniquely-named tenants/users.
/// </summary>
public sealed class TicketingApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();
    private readonly RedisContainer _redis = new RedisBuilder("redis:7").Build();

    /// <summary>Scriptable payment provider. Tests reset + stub it per scenario.</summary>
    public WireMockServer PaymentProvider { get; } = WireMockServer.Start();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Swap external endpoints before the host builds; everything else is production wiring.
        builder.UseSetting("ConnectionStrings:Default", _postgres.GetConnectionString());
        builder.UseSetting("ConnectionStrings:Redis", _redis.GetConnectionString());
        builder.UseSetting("PaymentProvider:BaseUrl", PaymentProvider.Urls[0] + "/");
        builder.UseSetting("PaymentProvider:RetryBaseDelayMs", "50"); // fast retry storms in tests
        builder.UseEnvironment("Development");
    }

    async Task IAsyncLifetime.InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

        // Touching Services builds the host, which (Development) migrates + seeds the admin.
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<TicketingDbContext>().Database.MigrateAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        PaymentProvider.Stop();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
    }
}

[CollectionDefinition(nameof(ApiCollection))]
public sealed class ApiCollection : ICollectionFixture<TicketingApiFactory>;
