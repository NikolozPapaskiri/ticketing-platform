using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketingPlatform.Domain;
using TicketingPlatform.Infrastructure.Persistence;
using TicketingPlatform.Infrastructure.Persistence.Migrations;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// Phase A slice 3. A data migration normally runs exactly once, in production, untested - so these
/// execute the SAME statements the migration does and assert the result.
///
/// The contract step ran the same backfill a second time and then made TicketType.PerformanceId
/// NOT NULL, which deliberately makes the old shape unrepresentable: a ticket type with no date can
/// no longer be inserted, so the half of this file that asserted ticket types being LINKED cannot
/// be written any more. The database now enforces what it used to check - see
/// ATicketTypeWithNoDate_IsRejectedByTheDatabase below, which is the replacement.
/// </summary>
[Collection(nameof(ApiCollection))]
public class PerformanceBackfillTests
{
    private readonly TicketingApiFactory _factory;
    public PerformanceBackfillTests(TicketingApiFactory factory) => _factory = factory;

    [Fact]
    public async Task EveryLegacyEventBecomesAOneNightRun()
    {
        var (tenantId, eventId, startsAt) = await SeedLegacyEventAsync();

        await RunBackfillAsync();

        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TicketingPlatform.Api.Tenancy.TenantContext>().SetTenant(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();

        var performance = Assert.Single(await db.Performances.Where(p => p.EventId == eventId).ToListAsync());
        Assert.Equal(PerformanceStatus.Scheduled, performance.Status);
        Assert.Equal(tenantId, performance.TenantId);            // tenancy is carried over, not lost
        // The synthetic date is the event's own date - a one-night run, which is what a flat event was.
        Assert.Equal(startsAt.ToUniversalTime(), performance.StartsAt.ToUniversalTime(), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ATicketTypeWithNoDate_IsRejectedByTheDatabase()
    {
        var (tenantId, eventId, _) = await SeedLegacyEventAsync();

        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TicketingPlatform.Api.Tenancy.TenantContext>().SetTenant(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
        db.TicketTypes.Add(new TicketType
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            EventId = eventId,
            Name = "Dateless",
            Price = 10m,
            Currency = "USD"
            // PerformanceId left unset: the legacy shape, now unrepresentable.
        });

        // The point of the contract step: the invariant is the schema's job, not a convention that
        // every future write path has to remember. Guid.Empty names no performance, so the foreign
        // key rejects it - there is no way to write a ticket type that belongs to no date.
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task RunningTheBackfillTwice_ChangesNothingTheSecondTime()
    {
        // Migrations are once-only, but a backfill that is not idempotent is a trap for reruns,
        // partial failures, and restores. The NOT EXISTS guard is what makes this safe - and it is
        // what let the contract step re-run the very same statements before adding the constraint.
        var (tenantId, eventId, _) = await SeedLegacyEventAsync();

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
        var (tenantId, eventId, _) = await SeedLegacyEventAsync();

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

    /// <summary>An event in the pre-slice-3 shape: a date on the event itself, no date row.</summary>
    private async Task<(Guid TenantId, Guid EventId, DateTimeOffset StartsAt)> SeedLegacyEventAsync()
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

        await db.SaveChangesAsync();
        return (tenantId, eventId, startsAt);
    }
}
