namespace TicketingPlatform.Domain;

/// <summary>
/// A physical location. Today `Event.VenueName` is free text, which cannot express the thing most
/// real ticketing sells: a hall with a fixed geometry, sold repeatedly across dates. A venue is
/// reusable across every event a tenant runs there.
/// </summary>
public class Venue
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }

    public required string Name { get; set; }
    public string? AddressLine { get; set; }
    public string? City { get; set; }
    public string? CountryCode { get; set; }

    /// <summary>
    /// The venue's own timezone. An organizer's "tonight" is the venue's tonight, not the server's -
    /// storing this per venue is what stops off-by-one-day bugs in listings for touring productions.
    /// </summary>
    public string? TimeZoneId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<Hall> Halls { get; set; } = new List<Hall>();
}

/// <summary>
/// An independently configurable space inside a venue (main auditorium, studio, standing floor).
/// Halls - not venues - own seat maps, because one building routinely sells several rooms at once
/// with unrelated layouts.
/// </summary>
public class Hall
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid VenueId { get; set; }
    public Venue Venue { get; set; } = null!;

    public required string Name { get; set; }

    /// <summary>
    /// Standing/GA rooms have capacity but no seats. Keeping this on the hall lets a GA performance
    /// use the existing counter model while a seated hall uses a seat map - the two coexist.
    /// </summary>
    public int? StandingCapacity { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<SeatMapVersion> SeatMapVersions { get; set; } = new List<SeatMapVersion>();
}
