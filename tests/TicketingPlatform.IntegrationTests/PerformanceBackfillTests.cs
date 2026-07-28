using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketingPlatform.Domain;
using TicketingPlatform.Infrastructure.Persistence;
using TicketingPlatform.Infrastructure.Persistence.Migrations;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// Phase A slice 3, the expand step. A data migration normally runs exactly once, in production,
/// untested - so these execute the SAME statements the migration does against deliberately
/// legacy-shaped rows (an event with ticket types and no performance) and assert the result.
/// </summary>
[Collection(nameof(ApiCollection))]
public class PerformanceBackfillTests
{
    private readonly TicketingApiFactory _factory;
    public PerformanceBackfillTests(TicketingApiFactory factory) => _factory = factory;

    [Fact]
    public async Task EveryLegacyEventBecomesAOneNightRun_WithItsTicketTypesAttached()
    {
        var (tenantId, eventId, startsAt) = await SeedLegacyEventAsync(ticketTypes: 2);

        await RunBackfillAsync();

        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TicketingPlatform.Api.Tenancy.TenantContext>().SetTenant(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();

        var performance = Assert.Single(await db.Performances.Where(p => p.EventId == eventId).ToListAsync());
        Assert.Equal(PerformanceStatus.Scheduled, performance.Status);
        Assert.Equal(tenantId, performance.TenantId);            // tenancy is carried over, not lost
        // The synthetic date is the event's own date - a one-night run, which is what a flat event was.
        Assert.Equal(startsAt.ToUniversalTime(), performance.StartsAt.ToUniversalTime(), TimeSpan.FromSeconds(1));

        var ticketTypes = await db.TicketTypes.Where(t => t.EventId == eventId).ToListAsync();
        Assert.Equal(2, ticketTypes.Count);
        Assert.All(ticketTypes, t => Assert.Equal(performance.Id, t.PerformanceId));
    }

    [Fact]
    public async Task RunningTheBackfillTwice_ChangesNothingTheSecondTime()
    {
        // Migrations are once-only, but a backfill that is not idempotent is a trap for reruns,
        // partial failures, and restores. The NOT EXISTS guard is what makes this safe.
        var (tenantId, eventId, _) = await SeedLegacyEventAsync(ticketTypes: 1);

        await RunBackfillAsync();
        await RunBackfillAsync();

        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TicketingPlatform.Api.Tenancy.TenantContext>().SetTenant(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();

        Assert.Single(await db.Performances.Where(p => p.EventId == eventId).ToListAsync());
    }

    [Fact]
    public async Task AnEventThatAlreadyHasRealDates_IsNotGivenASyntheticOne()
    {
        var (tenantId, eventId, _) = await SeedLegacyEventAsync(ticketTypes: 0);

        // Give it two genuine dates first, as a multi-date production would have.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
            for (var i = 1; i <= 2; i++)
            {
                db.Performances.Add(new Performance
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    EventId = eventId,
                    StartsAt = DateTimeOffset.UtcNow.AddDays(40 + i),
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }
            await db.SaveChangesAsync();
        }

        await RunBackfillAsync();

        using var check = _factory.Services.CreateScope();
        check.ServiceProvider.GetRequiredService<TicketingPlatform.Api.Tenancy.TenantContext>().SetTenant(tenantId);
        var db2 = check.ServiceProvider.GetRequiredService<TicketingDbContext>();

        // Still two: the backfill must not invent a third, phantom date.
        Assert.Equal(2, await db2.Performances.CountAsync(p => p.EventId == eventId));
    }

    private async Task RunBackfillAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
        await db.Database.ExecuteSqlRawAsync(PerformanceBackfill.CreateOnePerformancePerEvent);
        await db.Database.ExecuteSqlRawAsync(PerformanceBackfill.LinkTicketTypesToTheirPerformance);
    }

    /// <summary>An event in the pre-slice-3 shape: ticket types hanging off the event, no date row.</summary>
    private async Task<(Guid TenantId, Guid EventId, DateTimeOffset StartsAt)> SeedLegacyEventAsync(int ticketTypes)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();

        var tenantId = Guid.NewGuid();
        var startsAt = DateTimeOffset.UtcNow.AddDays(30);
        db.Tenants.Add(new Tenant { Id = tenantId, Name = $"T {tenantId:N}", Slug = $"t-{tenantId:N}" });

        var eventId = Guid.NewGuid();
        db.Events.Add(new Event
        {
            Id = eventId,
            TenantId = tenantId,
            Name = "Legacy show",
            VenueName = "QA Hall",
            StartsAt = startsAt
        });

        for (var i = 0; i < ticketTypes; i++)
        {
            var ticketTypeId = Guid.NewGuid();
            db.TicketTypes.Add(new TicketType
            {
                Id = ticketTypeId,
                TenantId = tenantId,
                EventId = eventId,
                Name = i == 0 ? "General Admission" : $"Tier {i}",
                Price = 25m + i,
                Currency = "USD"
                // PerformanceId deliberately left null: this is the legacy shape.
            });
            db.Inventories.Add(new Inventory
            {
                TicketTypeId = ticketTypeId,
                TenantId = tenantId,
                TotalQuantity = 10,
                AvailableQuantity = 10
            });
        }

        await db.SaveChangesAsync();
        return (tenantId, eventId, startsAt);
    }
}
