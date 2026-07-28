using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Domain;
using TicketingPlatform.Infrastructure.Persistence;
using TicketingPlatform.Infrastructure.Persistence.Scopes;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// Track 3 / T1. Pins the guarantees the access scopes are supposed to provide, against real
/// Postgres. These assert the MECHANISM, not an endpoint: the point of T1 is that isolation stops
/// depending on what each repository method remembered to write, so the tests hold the scopes
/// themselves to the rule.
/// </summary>
[Collection(nameof(ApiCollection))]
public class TenancyScopeTests
{
    private readonly TicketingApiFactory _factory;
    public TenancyScopeTests(TicketingApiFactory factory) => _factory = factory;

    /// <summary>A scope resolved with an explicit tenant (or none, for the tenant-less planes).</summary>
    private static IServiceScope ScopeWithTenant(TicketingApiFactory factory, Guid? tenantId)
    {
        var scope = factory.Services.CreateScope();
        if (tenantId is not null)
            scope.ServiceProvider.GetRequiredService<TicketingPlatform.Api.Tenancy.TenantContext>().SetTenant(tenantId.Value);
        return scope;
    }

    [Fact]
    public async Task CustomerScope_CannotReadAnotherCustomersOrder()
    {
        var (tenantId, orderId, ownerId) = await SeedOrderAsync();
        var intruderId = Guid.NewGuid();

        using var scope = ScopeWithTenant(_factory, tenantId);
        var customer = scope.ServiceProvider.GetRequiredService<CustomerScope>();

        // The owner sees it...
        Assert.NotNull(await customer.Orders(ownerId).FirstOrDefaultAsync(o => o.Id == orderId));

        // ...and another authenticated customer does not, even though both queries are
        // cross-tenant. Ownership is the default-deny on this plane, applied inside the scope.
        Assert.Null(await customer.Orders(intruderId).FirstOrDefaultAsync(o => o.Id == orderId));

        // Same guarantee for the transitively-owned ticket.
        Assert.Empty(await customer.Tickets(intruderId).Where(t => t.OrderId == orderId).ToListAsync());
    }

    [Fact]
    public async Task PublicScope_CannotReturnAnUnpublishedEvent()
    {
        var (tenantId, draftEventId) = await SeedDraftEventAsync();

        using var scope = ScopeWithTenant(_factory, tenantId);
        var publicScope = scope.ServiceProvider.GetRequiredService<PublicScope>();

        // The marketplace query bakes the OnSale predicate in, so a Draft event is invisible
        // without the caller having to remember to filter on status.
        Assert.Null(await publicScope.OnSaleEvents().FirstOrDefaultAsync(e => e.Id == draftEventId));

        // The deliberately-named escape still sees it - that is the one public read that can, and
        // naming it is what keeps it from spreading. See docs/MULTI_TENANCY.md.
        Assert.NotNull(await publicScope.EventsIncludingUnpublished().FirstOrDefaultAsync(e => e.Id == draftEventId));
    }

    [Fact]
    public void TenantScope_FailsClosedWithNoTenant()
    {
        using var scope = ScopeWithTenant(_factory, tenantId: null);
        var tenant = scope.ServiceProvider.GetRequiredService<TenantScope>();

        // Before T1 this silently returned zero rows: the filter compared TenantId == null and
        // matched nothing, which reads exactly like "this tenant has no events". Failing loudly is
        // the whole point - a silent empty result is the worst possible isolation failure.
        var error = Assert.Throws<InvalidOperationException>(() => tenant.Of<Event>());
        Assert.Contains("no tenant", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SystemScope_SeesAcrossTenants()
    {
        var (_, orderId, _) = await SeedOrderAsync();

        // No tenant at all - the background worker's view. This is the privilege the other scopes
        // are defined against.
        using var scope = ScopeWithTenant(_factory, tenantId: null);
        var system = scope.ServiceProvider.GetRequiredService<SystemScope>();

        Assert.NotNull(await system.Of<Order>().FirstOrDefaultAsync(o => o.Id == orderId));
    }

    // --- seeding -------------------------------------------------------------------------------

    private async Task<(Guid TenantId, Guid OrderId, Guid CustomerUserId)> SeedOrderAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();

        var tenantId = Guid.NewGuid();
        var customerUserId = Guid.NewGuid();
        var (eventId, ticketTypeId, holdId) = await SeedGraphAsync(db, tenantId, customerUserId);

        var orderId = Guid.NewGuid();
        db.Orders.Add(new Order
        {
            Id = orderId,
            TenantId = tenantId,
            HoldId = holdId,
            CustomerUserId = customerUserId,
            CustomerEmail = $"owner-{customerUserId:N}@test.local",
            Amount = 25m,
            Currency = "USD",
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.Tickets.Add(new Ticket
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            OrderId = orderId,
            Code = Guid.NewGuid().ToString("N"),
            FilePath = $"tickets/{tenantId}/{orderId}.pdf",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        _ = eventId; _ = ticketTypeId;
        return (tenantId, orderId, customerUserId);
    }

    private async Task<(Guid TenantId, Guid EventId)> SeedDraftEventAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();

        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = $"T {tenantId:N}", Slug = $"t-{tenantId:N}" });

        var eventId = Guid.NewGuid();
        db.Events.Add(new Event
        {
            Id = eventId,
            TenantId = tenantId,
            Name = "Draft show",
            VenueName = "QA Hall",
            StartsAt = DateTimeOffset.UtcNow.AddDays(30)   // Draft is the default: never published
        });
        await db.SaveChangesAsync();

        return (tenantId, eventId);
    }

    private static async Task<(Guid EventId, Guid TicketTypeId, Guid HoldId)> SeedGraphAsync(
        TicketingDbContext db, Guid tenantId, Guid customerUserId)
    {
        db.Tenants.Add(new Tenant { Id = tenantId, Name = $"T {tenantId:N}", Slug = $"t-{tenantId:N}" });

        var eventId = Guid.NewGuid();
        var onSale = new Event
        {
            Id = eventId,
            TenantId = tenantId,
            Name = "Scoped show",
            VenueName = "QA Hall",
            StartsAt = DateTimeOffset.UtcNow.AddDays(30)
        };
        onSale.TransitionTo(EventStatus.OnSale); // Status is state-machine guarded, not settable
        db.Events.Add(onSale);

        var performanceId = Guid.NewGuid();
        db.Performances.Add(new Performance
        {
            Id = performanceId,
            TenantId = tenantId,
            EventId = eventId,
            StartsAt = DateTimeOffset.UtcNow.AddDays(30),
            CreatedAt = DateTimeOffset.UtcNow
        });

        var ticketTypeId = Guid.NewGuid();
        db.TicketTypes.Add(new TicketType
        {
            Id = ticketTypeId,
            TenantId = tenantId,
            EventId = eventId,
            PerformanceId = performanceId, // required since the contract step: a price is per date
            Name = "General Admission",
            Price = 25m,
            Currency = "USD"
        });
        db.Inventories.Add(new Inventory
        {
            TicketTypeId = ticketTypeId,
            TenantId = tenantId,
            TotalQuantity = 10,
            AvailableQuantity = 9
        });

        var holdId = Guid.NewGuid();
        db.Holds.Add(new Hold
        {
            Id = holdId,
            TenantId = tenantId,
            TicketTypeId = ticketTypeId,
            CustomerUserId = customerUserId,
            Quantity = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        });

        await db.SaveChangesAsync();
        return (eventId, ticketTypeId, holdId);
    }
}
