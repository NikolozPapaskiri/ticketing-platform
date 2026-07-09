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

    /// <summary>Storage-relative path, e.g. "tickets/{tenantId}/{orderId}.pdf".</summary>
    public required string FilePath { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
