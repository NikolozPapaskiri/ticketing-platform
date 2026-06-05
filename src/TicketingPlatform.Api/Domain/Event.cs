namespace TicketingPlatform.Api.Domain;

/// <summary>
/// An event put on sale by a tenant. Tenant-scoped: TenantId plus a global query filter
/// in the DbContext mean every read is automatically limited to the current tenant.
/// </summary>
public class Event
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }

    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? VenueName { get; set; }

    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<TicketType> TicketTypes { get; set; } = new List<TicketType>();

    public EventStatus Status { get; private set; } = EventStatus.Draft;   // <-- private set + default
    // ...CreatedAt, TicketTypes unchanged...

    // The single reviewable "what's allowed" table.
    private static readonly Dictionary<EventStatus, EventStatus[]> AllowedTransactions = new()
    {
        [EventStatus.Draft] = new[] { EventStatus.OnSale, EventStatus.Closed },
        [EventStatus.OnSale] = new[] { EventStatus.Closed },
        [EventStatus.Closed] = Array.Empty<EventStatus>(), //Terminal
    };

    public bool CantransitionTo(EventStatus target) =>
        AllowedTransactions.TryGetValue(target, out var targets) && targets.Contains(target);

    public void TransitionTo(EventStatus target)
    {
        if (!CantransitionTo(target))
        {
            throw new InvalidOperationException($"Cannot transition from {Status} to {target}");   // defense-in-depth backstop
        }
        Status = target;
    }
}

public enum EventStatus
{
    Draft,
    OnSale,
    Closed
}
