using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TicketingPlatform.Domain;
using TicketingPlatform.Infrastructure.Persistence;
using TicketingPlatform.Infrastructure.Persistence.Scopes;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// Phase A, slice 1: the venue geometry actually persists, obeys the same tenancy rule as every
/// other tenant-owned entity, and enforces seat IDENTITY in the database rather than in application
/// code - a unique constraint is a stronger and cheaper guarantee than a check somebody remembers.
/// </summary>
[Collection(nameof(ApiCollection))]
public class VenueGeometryTests
{
    private readonly TicketingApiFactory _factory;
    public VenueGeometryTests(TicketingApiFactory factory) => _factory = factory;

    [Fact]
    public async Task AHallSeatMapRoundTripsAsAGraph()
    {
        var (tenantId, mapId) = await SeedSeatMapAsync();

        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TicketingPlatform.Api.Tenancy.TenantContext>().SetTenant(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();

        var map = await db.SeatMapVersions
            .Include(m => m.Sections).ThenInclude(s => s.Rows).ThenInclude(r => r.Seats)
            .FirstAsync(m => m.Id == mapId);

        Assert.True(map.IsPublished);
        var section = Assert.Single(map.Sections);
        var row = Assert.Single(section.Rows);
        Assert.Equal(2, row.Seats.Count);
        Assert.Contains(row.Seats, s => s.Number == "12" && s.Kind == SeatKind.Sellable);
        Assert.Contains(row.Seats, s => s.Kind == SeatKind.Accessible);
    }

    [Fact]
    public async Task SeatIdentityIsUniqueWithinARow_EnforcedByTheDatabase()
    {
        var (tenantId, mapId) = await SeedSeatMapAsync();

        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TicketingPlatform.Api.Tenancy.TenantContext>().SetTenant(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();

        var row = await db.SeatRows.FirstAsync(r => r.Section.SeatMapVersionId == mapId);

        // "Seat 12" must mean one seat: the door reads that off the ticket.
        db.Seats.Add(new Seat { Id = Guid.NewGuid(), TenantId = tenantId, SeatRowId = row.Id, Number = "12" });

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ((PostgresException)error.InnerException!).SqlState);
    }

    [Fact]
    public async Task VenuesAreTenantScopedLikeEveryOtherOperationalEntity()
    {
        var (tenantId, _) = await SeedSeatMapAsync();

        using var scope = _factory.Services.CreateScope();
        var tenantCtx = scope.ServiceProvider.GetRequiredService<TicketingPlatform.Api.Tenancy.TenantContext>();

        // A DIFFERENT tenant cannot see it - the same global filter that governs events and orders.
        tenantCtx.SetTenant(Guid.NewGuid());
        var tenantScope = scope.ServiceProvider.GetRequiredService<TenantScope>();
        Assert.Empty(await tenantScope.Of<Venue>().Where(v => v.TenantId == tenantId).ToListAsync());

        // ...while the background plane, which has no tenant at all, still can.
        var system = scope.ServiceProvider.GetRequiredService<SystemScope>();
        Assert.NotEmpty(await system.Of<Venue>().Where(v => v.TenantId == tenantId).ToListAsync());
    }

    private async Task<(Guid TenantId, Guid SeatMapVersionId)> SeedSeatMapAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();

        var tenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.Tenants.Add(new Tenant { Id = tenantId, Name = $"T {tenantId:N}", Slug = $"t-{tenantId:N}" });

        var venue = new Venue
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "Rustaveli Theatre",
            City = "Tbilisi",
            CountryCode = "GE",
            TimeZoneId = "Asia/Tbilisi",
            CreatedAt = now
        };
        var hall = new Hall
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            VenueId = venue.Id,
            Name = "Main Auditorium",
            CreatedAt = now
        };
        var map = new SeatMapVersion
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            HallId = hall.Id,
            Version = 1,
            CreatedAt = now
        };

        var stalls = map.AddSection(Guid.NewGuid(), "Stalls", 1);
        var rowA = stalls.AddRow(Guid.NewGuid(), "A", 1);
        rowA.AddSeat(Guid.NewGuid(), "12", x: 10.5m, y: 4m);
        var accessible = rowA.AddSeat(Guid.NewGuid(), "13", x: 12m, y: 4m);
        accessible.Kind = SeatKind.Accessible;
        map.Publish(now);

        db.Venues.Add(venue);
        db.Halls.Add(hall);
        db.SeatMapVersions.Add(map);
        await db.SaveChangesAsync();

        return (tenantId, map.Id);
    }
}
