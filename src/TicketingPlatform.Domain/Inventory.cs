namespace TicketingPlatform.Domain;

/// <summary>
/// Available stock for a ticket type. AvailableQuantity is the contested resource that Phase 5
/// must decrement safely under concurrent purchases. The DbContext maps Postgres's system xmin
/// column as an optimistic-concurrency token for this entity (no explicit property needed).
/// </summary>
public class Inventory
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }

    public Guid TicketTypeId { get; set; }
    public TicketType TicketType { get; set; } = null!;

    public int TotalQuantity { get; set; }
    public int AvailableQuantity { get; set; }

    /// <summary>
    /// Attempts to reserve quantity for a hold. Returns false instead of throwing: an
    /// insufficient-stock request is an expected outcome (mapped to 409 upstream), not a bug.
    /// NOTE: correct single-threaded only. Two concurrent callers can both pass the check
    /// before either saves - that race is exactly the Phase 5 oversell problem; the xmin
    /// concurrency token on this entity is the first of the three defenses built there.
    /// </summary>
    public bool TryReserve(int quantity)
    {
        if (quantity <= 0 || quantity > AvailableQuantity)
            return false;

        AvailableQuantity -= quantity;
        return true;
    }

    /// <summary>Returns previously reserved quantity (hold released or expired).</summary>
    public void Release(int quantity)
    {
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Release quantity must be positive.");

        // Clamp: available can never exceed capacity, even if a caller double-releases.
        AvailableQuantity = Math.Min(TotalQuantity, AvailableQuantity + quantity);
    }
}
