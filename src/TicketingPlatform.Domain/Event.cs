namespace TicketingPlatform.Domain;

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

    /// <summary>Marketplace browse category (tkt.ge-style icon navigation).</summary>
    public EventCategory Category { get; set; } = EventCategory.Other;

    /// <summary>Storage-relative path of the event image; null = no image (UI shows a placeholder).</summary>
    public string? ImagePath { get; private set; }

    /// <summary>
    /// When true, customers must pass through the virtual waiting room (queue-based load
    /// leveling) before they can reserve tickets. Organizers flip this on for on-sales whose
    /// demand would otherwise stampede the inventory row.
    /// </summary>
    public bool WaitingRoomEnabled { get; set; }

    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<TicketType> TicketTypes { get; set; } = new List<TicketType>();

    public EventStatus Status { get; private set; } = EventStatus.Draft;

    // The single reviewable "what's allowed" table.
    private static readonly Dictionary<EventStatus, EventStatus[]> AllowedTransitions = new()
    {
        [EventStatus.Draft] = new[] { EventStatus.OnSale, EventStatus.Closed },
        [EventStatus.OnSale] = new[] { EventStatus.Closed },
        [EventStatus.Closed] = Array.Empty<EventStatus>(), //Terminal
    };

    public bool CanTransitionTo(EventStatus target) =>
        AllowedTransitions.TryGetValue(Status, out var allowed) && allowed.Contains(target);

    public void UpdateDetails(string name, string? description, string? venueName, DateTimeOffset startsAt, EventCategory category)
    {
        Name = name;
        Description = description;
        VenueName = venueName;
        StartsAt = startsAt;
        Category = category;
    }

    public void SetImage(string imagePath) => ImagePath = imagePath;

    public void TransitionTo(EventStatus target)
    {
        // Defense-in-depth backstop. Callers should pre-check CanTransitionTo and return a clean 409;
        // this throw protects state integrity if some future path forgets to.
        if (!CanTransitionTo(target))
        {
            throw new InvalidStatusTransitionException(Status, target);
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

/// <summary>Marketplace categories, mirroring the icon navigation of consumer ticketing sites.</summary>
public enum EventCategory
{
    Other,
    Concert,
    Theatre,
    Opera,
    Sport,
    Cinema,
    Festival,
    StandUp,
    Conference,
    Kids
}
