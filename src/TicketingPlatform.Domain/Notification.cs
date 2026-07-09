namespace TicketingPlatform.Domain;

/// <summary>
/// What the message consumer produces: a record that "someone should be told about this".
/// A real system would render and send email/SMS; the record is the seam where that plugs in.
/// </summary>
public class Notification
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Type { get; set; }
    public required string Message { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
