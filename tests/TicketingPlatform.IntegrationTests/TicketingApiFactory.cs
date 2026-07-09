using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using TicketingPlatform.Infrastructure.Persistence;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// Boots the real API in-memory (WebApplicationFactory) against a throwaway PostgreSQL 17
/// container (Testcontainers). One container + one host for the whole test run (collection
/// fixture): container startup costs seconds, so per-test containers would be prohibitively
/// slow; tests keep themselves independent by creating uniquely-named tenants instead.
/// The real migrations run against the real provider - this is the whole point: no mocked
/// DbContext, no SQLite stand-in, the SQL that runs in production runs here.
/// </summary>
public sealed class TicketingApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Swap the connection string before the host builds; everything else is production wiring.
        builder.UseSetting("ConnectionStrings:Default", _postgres.GetConnectionString());
        builder.UseEnvironment("Development");
    }

    async Task IAsyncLifetime.InitializeAsync()
    {
        await _postgres.StartAsync();

        // The API does not auto-migrate on startup (deliberate), so the fixture applies the
        // real migrations once the container is reachable. Touching Services also builds the host.
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
        await db.Database.MigrateAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync().AsTask();
    }
}

[CollectionDefinition(nameof(ApiCollection))]
public sealed class ApiCollection : ICollectionFixture<TicketingApiFactory>;
