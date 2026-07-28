using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketingPlatform.Domain;
using TicketingPlatform.Infrastructure.Persistence;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// Phase A, slice 2: one event, many dates, persisted. Proves the thing cloning-an-event-per-date
/// cannot do - shared content with independent per-date state - and that the split is additive, so
/// the existing GA path is untouched.
/// </summary>
[Collection(nameof(ApiCollection))]
public class PerformanceScheduleTests
{
    private readonly TicketingApiFactory _factory;
    public PerformanceScheduleTests(TicketingApiFactory factory) => _factory = factory;

    [Fact]
    public async Task OneEventCarriesManyDates_AndCancellingOneLeavesTheRest()
    {
        var (tenantId, eventId) = await SeedRunAsync(nights: 3);

        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TicketingPlatform.Api.Tenancy.TenantContext>().SetTenant(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();

        var run = await db.Events
            .Include(e => e.Performances)
            .FirstAsync(e => e.Id == eventId);
        Assert.Equal(3, run.Performances.Count);

        // Cancel the middle night only.
        var middle = run.Performances.OrderBy(p => p.StartsAt).Skip(1).First();
        middle.Cancel(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        var after = await db.Performances.Where(p => p.EventId == eventId).ToListAsync();
        Assert.Equal(1, after.Count(p => p.Status == PerformanceStatus.Cancelled));
        Assert.Equal(2, after.Count(p => p.Status == PerformanceStatus.Scheduled));
        // Content lives on the event, so the surviving dates still share it - no drift.
        Assert.Equal("Hamlet", run.Name);
    }

    [Fact]
    public async Task APerformancePinsTheSeatMapVersionItSells()
    {
        var (tenantId, eventId) = await SeedRunAsync(nights: 1, withGeometry: true);

        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TicketingPlatform.Api.Tenancy.TenantContext>().SetTenant(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();

        var performance = await db.Performances
            .Include(p => p.SeatMapVersion)
            .Include(p => p.Hall)
            .FirstAsync(p => p.EventId == eventId);

        // Pinning the VERSION (not just the hall) is what lets the hall be re-striped later without
        // rewriting the seats printed on tickets already sold for this date.
        Assert.NotNull(performance.Hall);
        Assert.NotNull(performance.SeatMapVersion);
        Assert.True(performance.SeatMapVersion!.IsPublished);
    }

    [Fact]
    public async Task TheExistingGeneralAdmissionPathIsUntouched()
    {
        // The split is additive: an event with ticket types and no performances still behaves
        // exactly as before, which is what keeps this slice releasable on its own.
        var (tenantId, eventId) = await SeedRunAsync(nights: 0);

        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TicketingPlatform.Api.Tenancy.TenantContext>().SetTenant(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();

        var run = await db.Events.Include(e => e.Performances).FirstAsync(e => e.Id == eventId);
        Assert.Empty(run.Performances);
        Assert.True(run.StartsAt > DateTimeOffset.UtcNow); // Event.StartsAt still drives GA today
    }

    private async Task<(Guid TenantId, Guid EventId)> SeedRunAsync(int nights, bool withGeometry = false)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();

        var tenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.Tenants.Add(new Tenant { Id = tenantId, Name = $"T {tenantId:N}", Slug = $"t-{tenantId:N}" });

        var run = new Event
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "Hamlet",
            VenueName = "Rustaveli Theatre",
            StartsAt = now.AddDays(30)
        };
        db.Events.Add(run);

        Guid? hallId = null, mapId = null;
        if (withGeometry)
        {
            var venue = new Venue { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Rustaveli", CreatedAt = now };
            var hall = new Hall { Id = Guid.NewGuid(), TenantId = tenantId, VenueId = venue.Id, Name = "Main", CreatedAt = now };
            var map = new SeatMapVersion
            {
                Id = Guid.NewGuid(), TenantId = tenantId, HallId = hall.Id, Version = 1, CreatedAt = now
            };
            map.AddSection(Guid.NewGuid(), "Stalls", 1).AddRow(Guid.NewGuid(), "A", 1).AddSeat(Guid.NewGuid(), "1");
            map.Publish(now);
            db.Venues.Add(venue);
            db.Halls.Add(hall);
            db.SeatMapVersions.Add(map);
            hallId = hall.Id;
            mapId = map.Id;
        }

        for (var i = 0; i < nights; i++)
        {
            db.Performances.Add(new Performance
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                EventId = run.Id,
                HallId = hallId,
                SeatMapVersionId = mapId,
                StartsAt = now.AddDays(30 + i),
                DoorsOpenAt = now.AddDays(30 + i).AddMinutes(-45),
                CreatedAt = now
            });
        }

        await db.SaveChangesAsync();
        return (tenantId, run.Id);
    }
}
