namespace TicketingPlatform.Domain;

/// <summary>
/// Server-side record for API idempotency keys. The key is scoped by tenant and actor, and the
/// request hash prevents a client from reusing the same key for a different operation.
/// </summary>
public class IdempotencyRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string ActorKey { get; set; }
    public required string Key { get; set; }
    public required string RequestHash { get; set; }
    public Guid OrderId { get; set; }
    public IdempotencyRecordStatus Status { get; private set; } = IdempotencyRecordStatus.InProgress;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public void Complete(DateTimeOffset completedAt)
    {
        Status = IdempotencyRecordStatus.Completed;
        CompletedAt = completedAt;
    }
}

public enum IdempotencyRecordStatus
{
    InProgress,
    Completed
}
