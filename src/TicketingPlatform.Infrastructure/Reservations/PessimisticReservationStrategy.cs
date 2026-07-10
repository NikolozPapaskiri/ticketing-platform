using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Application.Common;
using TicketingPlatform.Domain;
using TicketingPlatform.Infrastructure.Persistence;

namespace TicketingPlatform.Infrastructure.Reservations;

/// <summary>
/// Pessimistic locking: SELECT ... FOR UPDATE takes a row lock, so competing buyers QUEUE at
/// the database instead of colliding. Always correct, no retries, no wasted work - at the cost
/// of serializing all throughput on the hot row and holding a lock for the transaction's
/// lifetime (keep that transaction SHORT; a lock held across an external call is an outage).
/// The FOR UPDATE select drops to raw ADO deliberately: EF composes query filters around raw
/// SQL, and Postgres rejects FOR UPDATE inside the wrapper subquery - so the tenant predicate
/// is written by hand here. When the ORM fights the exact SQL you need, use the exact SQL.
/// </summary>
public sealed class PessimisticReservationStrategy : IReservationStrategy
{
    private readonly TicketingDbContext _db;
    public PessimisticReservationStrategy(TicketingDbContext db) => _db = db;

    public async Task<Result> ReserveAsync(Hold hold, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Lock the row (or wait for whoever holds it). Everything after this line sees a
        // frozen row that nobody else can change until we commit.
        var available = await SelectForUpdateAsync(hold.TicketTypeId, hold.TenantId, tx, ct);
        if (available is null)
        {
            return Result.NotFound($"Ticket type '{hold.TicketTypeId}' was not found.");
        }

        if (hold.Quantity <= 0 || hold.Quantity > available)
        {
            return Result.Conflict($"Cannot hold {hold.Quantity} tickets; only {available} available.");
        }

        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Inventories" SET "AvailableQuantity" = "AvailableQuantity" - {hold.Quantity}
            WHERE "TicketTypeId" = {hold.TicketTypeId} AND "TenantId" = {hold.TenantId}
            """, ct);

        _db.Holds.Add(hold);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct); // lock released here

        return Result.Success();
    }

    public async Task ReleaseAsync(Hold hold, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Clamped at capacity in SQL - the same "no minting stock" rule the domain enforces.
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Inventories" SET "AvailableQuantity" = LEAST("TotalQuantity", "AvailableQuantity" + {hold.Quantity})
            WHERE "TicketTypeId" = {hold.TicketTypeId} AND "TenantId" = {hold.TenantId}
            """, ct);

        try
        {
            await _db.SaveChangesAsync(ct); // persists the hold's Released/Expired status flip
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another writer already released/expired this hold. The status flip's concurrency
            // token no longer matches, so the whole transaction rolls back and the inventory
            // increment above never lands - the seat is credited exactly once (idempotent).
            await tx.RollbackAsync(ct);
        }
    }

    private async Task<int?> SelectForUpdateAsync(Guid ticketTypeId, Guid tenantId, IDbContextTransaction tx, CancellationToken ct)
    {
        var connection = _db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = tx.GetDbTransaction(); // enlist in the EF transaction
        command.CommandText = """
            SELECT "AvailableQuantity" FROM "Inventories"
            WHERE "TicketTypeId" = @ticketTypeId AND "TenantId" = @tenantId
            FOR UPDATE
            """;

        var p1 = command.CreateParameter(); p1.ParameterName = "ticketTypeId"; p1.Value = ticketTypeId;
        var p2 = command.CreateParameter(); p2.ParameterName = "tenantId"; p2.Value = tenantId;
        command.Parameters.Add(p1);
        command.Parameters.Add(p2);

        var result = await command.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : (int)result;
    }
}
