using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketingPlatform.Domain;
using TicketingPlatform.Infrastructure.Persistence;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// Phase A slice 3, the migrate step - the WRITE half. The backfill fixed the rows that already
/// existed; these pin that new writes stop producing the old shape, which is the precondition for
/// the contract step making TicketType.PerformanceId required.
/// </summary>
[Collection(nameof(ApiCollection))]
public class PerformanceWriteThroughTests
{
    private readonly TicketingApiFactory _factory;
    private readonly HttpClient _client;

    public PerformanceWriteThroughTests(TicketingApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task CreatingAnEvent_CreatesTheDateItPlaysOn()
    {
        var (tenant, staff) = await _client.CreateTenantWithStaffAsync();
        var ev = await _client.CreateEventAsync(staff);

        var performance = Assert.Single(await PerformancesOfAsync(tenant.Id, ev.Id));
        Assert.Equal(PerformanceStatus.Scheduled, performance.Status);
        Assert.Equal(tenant.Id, performance.TenantId);
        // The date the caller sent is now a row, not just a column on the event.
        Assert.Equal(ev.StartsAt.ToUnixTimeSeconds(), performance.StartsAt.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task AddingATicketType_AttachesItToTheEventsDate()
    {
        var (tenant, staff) = await _client.CreateTenantWithStaffAsync();
        var ev = await _client.CreateEventAsync(staff);

        var response = await _client.PostAsAsync(staff, $"/api/v1/events/{ev.Id}/ticket-types",
            new { name = "GA", price = 30m, currency = "USD", totalQuantity = 10 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var performance = Assert.Single(await PerformancesOfAsync(tenant.Id, ev.Id));
        using var scope = TenantScope(tenant.Id);
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
        var ticketType = Assert.Single(await db.TicketTypes.Where(tt => tt.EventId == ev.Id).ToListAsync());

        // Price and capacity are per-date; a ticket type that belongs to no date cannot be either.
        Assert.Equal(performance.Id, ticketType.PerformanceId);
    }

    [Fact]
    public async Task MovingTheEventsDate_MovesThePerformance_RatherThanLeavingItBehind()
    {
        var (tenant, staff) = await _client.CreateTenantWithStaffAsync();
        var ev = await _client.CreateEventAsync(staff);
        var moved = DateTimeOffset.UtcNow.AddMonths(3);

        var update = await _client.PutAsAsync(staff, $"/api/v1/events/{ev.Id}",
            new { name = "Moved", venueName = "Main Hall", startsAt = moved });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        // Still one date, and it is the new one: an edit moves the run, it does not fork it.
        var performance = Assert.Single(await PerformancesOfAsync(tenant.Id, ev.Id));
        Assert.Equal(moved.ToUnixTimeSeconds(), performance.StartsAt.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task AnEventWithSeveralDates_IsNotMovedByAnEventLevelEdit()
    {
        var (tenant, staff) = await _client.CreateTenantWithStaffAsync();
        var ev = await _client.CreateEventAsync(staff);

        // A second night, as a real run would have.
        var secondNight = DateTimeOffset.UtcNow.AddMonths(2);
        using (var seed = TenantScope(tenant.Id))
        {
            var db = seed.ServiceProvider.GetRequiredService<TicketingDbContext>();
            db.Performances.Add(new Performance
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                EventId = ev.Id,
                StartsAt = secondNight,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var update = await _client.PutAsAsync(staff, $"/api/v1/events/{ev.Id}",
            new { name = "Edited", startsAt = DateTimeOffset.UtcNow.AddMonths(9) });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        // "The event moved to the 14th" means nothing for a run of several nights, and guessing
        // which night was meant would silently move the wrong one. Both dates stay put.
        var dates = (await PerformancesOfAsync(tenant.Id, ev.Id))
            .Select(p => p.StartsAt.ToUnixTimeSeconds())
            .OrderBy(t => t)
            .ToList();
        Assert.Equal(
            new[] { ev.StartsAt.ToUnixTimeSeconds(), secondNight.ToUnixTimeSeconds() }.OrderBy(t => t).ToList(),
            dates);
    }

    private IServiceScope TenantScope(Guid tenantId)
    {
        var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TicketingPlatform.Api.Tenancy.TenantContext>().SetTenant(tenantId);
        return scope;
    }

    private async Task<List<Performance>> PerformancesOfAsync(Guid tenantId, Guid eventId)
    {
        using var scope = TenantScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
        return await db.Performances.AsNoTracking().Where(p => p.EventId == eventId).ToListAsync();
    }
}
