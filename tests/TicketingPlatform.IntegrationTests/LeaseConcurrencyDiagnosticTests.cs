using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketingPlatform.Domain;
using TicketingPlatform.Infrastructure.Persistence;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// Instrument before asserting: does extending a PaymentPending hold's lease actually trip the
/// row's concurrency token when someone else moves the row underneath? G2's conflict branch is
/// unreachable in practice if it does not, so this is worth pinning independently of the HTTP path.
/// </summary>
[Collection(nameof(ApiCollection))]
public class LeaseConcurrencyDiagnosticTests
{
    private readonly TicketingApiFactory _factory;
    public LeaseConcurrencyDiagnosticTests(TicketingApiFactory factory) => _factory = factory;

    [Fact]
    public async Task ExtendingALeaseAfterAnotherWriterMovedTheRow_ConflictsOnXmin()
    {
        var holdId = await SeedPaymentPendingHoldAsync();

        // Reader A loads the hold, capturing its xmin as the original value.
        using var scopeA = _factory.Services.CreateScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<TicketingDbContext>();
        var hold = await dbA.Holds.IgnoreQueryFilters().FirstAsync(h => h.Id == holdId);

        // Writer B moves the row out from under it (the reconciler / another replica).
        using (var scopeB = _factory.Services.CreateScope())
        {
            var dbB = scopeB.ServiceProvider.GetRequiredService<TicketingDbContext>();
            var rows = await dbB.Holds.IgnoreQueryFilters()
                .Where(h => h.Id == holdId)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    h => h.PaymentLeaseUntil, DateTimeOffset.UtcNow.AddMinutes(45)));
            Assert.Equal(1, rows);
        }

        // A now extends the lease on its stale snapshot. A DIFFERENT value is essential: writing
        // the value the row already has produces no UPDATE at all, and therefore no conflict.
        hold.ExtendPaymentLease(DateTimeOffset.UtcNow.AddMinutes(90));
        Assert.True(dbA.Entry(hold).Property(h => h.PaymentLeaseUntil).IsModified,
            "EF saw no change to the lease, so it would emit no UPDATE and never conflict");

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbA.SaveChangesAsync());
    }

    private async Task<Guid> SeedPaymentPendingHoldAsync()
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
            Name = "Lease diagnostic",
            VenueName = "QA Hall",
            StartsAt = DateTimeOffset.UtcNow.AddDays(30)
        });

        var ticketTypeId = Guid.NewGuid();
        db.TicketTypes.Add(new TicketType
        {
            Id = ticketTypeId,
            TenantId = tenantId,
            EventId = eventId,
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

        var now = DateTimeOffset.UtcNow;
        var holdId = Guid.NewGuid();
        var hold = new Hold
        {
            Id = holdId,
            TenantId = tenantId,
            TicketTypeId = ticketTypeId,
            Quantity = 1,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(10)
        };
        hold.ClaimForPayment(now, now.AddMinutes(10)); // Active -> PaymentPending, with a lease
        db.Holds.Add(hold);

        await db.SaveChangesAsync();
        return holdId;
    }
}
