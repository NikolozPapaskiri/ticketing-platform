namespace TicketingPlatform.Domain;

/// <summary>
/// An issued ticket document: metadata in the database, the PDF itself in object/file storage
/// (never large blobs in the relational DB). Issued asynchronously by the ticket-issuer
/// consumer when OrderConfirmed flows through the broker.
/// </summary>
public class Ticket
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OrderId { get; set; }

    /// <summary>Opaque validation code printed on the ticket PDF and checked at entry.</summary>
    public required string Code { get; set; }

    /// <summary>Storage-relative path, e.g. "tickets/{tenantId}/{orderId}.pdf".</summary>
    public required string FilePath { get; set; }

    public TicketStatus Status { get; private set; } = TicketStatus.Issued;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ScannedAt { get; private set; }
    public DateTimeOffset? VoidedAt { get; private set; }

    public void MarkScanned(DateTimeOffset scannedAt)
    {
        if (Status != TicketStatus.Issued)
            throw new InvalidOperationException($"Cannot scan a ticket in status '{Status}'.");
        Status = TicketStatus.Scanned;
        ScannedAt = scannedAt;
    }

    public void Void(DateTimeOffset voidedAt)
    {
        if (Status == TicketStatus.Voided)
            return;
        Status = TicketStatus.Voided;
        VoidedAt = voidedAt;
    }
}

public enum TicketStatus
{
    Issued,
    Scanned,
    Voided
}
