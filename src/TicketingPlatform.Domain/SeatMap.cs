namespace TicketingPlatform.Domain;

/// <summary>
/// An IMMUTABLE snapshot of a hall's layout.
///
/// Immutability is the whole point, and it is the part that cannot be retrofitted. A ticket sold
/// last season says "Balcony B, Row 4, Seat 12"; if re-striping the hall edited those rows in
/// place, every already-sold ticket would silently start describing a different seat. So a layout
/// change publishes a NEW version, and sold tickets keep pointing at the version they were sold
/// against.
///
/// The lifecycle is therefore: build it while Draft, then <see cref="Publish"/> it - after which
/// its geometry is frozen and only a new version can express a change.
/// </summary>
public class SeatMapVersion
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid HallId { get; set; }
    public Hall Hall { get; set; } = null!;

    /// <summary>Monotonic per hall: v1, v2, ... Human-facing and stable once published.</summary>
    public int Version { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; private set; }

    /// <summary>Frozen once published. Every structural mutation checks this.</summary>
    public bool IsPublished => PublishedAt is not null;

    public ICollection<Section> Sections { get; set; } = new List<Section>();

    public void Publish(DateTimeOffset now)
    {
        if (IsPublished)
            throw new InvalidOperationException($"Seat map version {Version} is already published.");
        PublishedAt = now;
    }

    /// <summary>
    /// Guard for every structural edit. Called by the aggregate's own mutators rather than by
    /// callers, so "published maps never change" holds by construction instead of by reviewer
    /// vigilance - the same argument as the tenancy access scopes.
    /// </summary>
    public void EnsureEditable()
    {
        if (IsPublished)
            throw new InvalidOperationException(
                $"Seat map version {Version} is published and immutable. Create a new version to change the layout.");
    }

    public Section AddSection(Guid id, string name, int displayOrder)
    {
        EnsureEditable();
        var section = new Section
        {
            Id = id,
            TenantId = TenantId,
            SeatMapVersionId = Id,
            Name = name,
            DisplayOrder = displayOrder
        };
        Sections.Add(section);
        return section;
    }
}

/// <summary>A named block of the hall ("Stalls", "Balcony B"). Part of the printed seat identity.</summary>
public class Section
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid SeatMapVersionId { get; set; }
    public SeatMapVersion SeatMapVersion { get; set; } = null!;

    public required string Name { get; set; }
    public int DisplayOrder { get; set; }

    public ICollection<SeatRow> Rows { get; set; } = new List<SeatRow>();

    public SeatRow AddRow(Guid id, string label, int displayOrder)
    {
        SeatMapVersion?.EnsureEditable();
        var row = new SeatRow
        {
            Id = id,
            TenantId = TenantId,
            SectionId = Id,
            Label = label,
            DisplayOrder = displayOrder
        };
        Rows.Add(row);
        return row;
    }
}

/// <summary>
/// A row within a section. Named SeatRow rather than Row to avoid colliding with the data-access
/// vocabulary ("row") that surrounds this codebase in repositories and SQL discussions.
/// </summary>
public class SeatRow
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid SectionId { get; set; }
    public Section Section { get; set; } = null!;

    /// <summary>Printed label - "4", "AA", "M". Not an index: rows are not always numbered.</summary>
    public required string Label { get; set; }
    public int DisplayOrder { get; set; }

    public ICollection<Seat> Seats { get; set; } = new List<Seat>();

    public Seat AddSeat(Guid id, string number, decimal? x = null, decimal? y = null)
    {
        var seat = new Seat
        {
            Id = id,
            TenantId = TenantId,
            SeatRowId = Id,
            Number = number,
            MapX = x,
            MapY = y
        };
        Seats.Add(seat);
        return seat;
    }
}

/// <summary>
/// One seat, in one version of one layout.
///
/// The two concerns here are deliberately separate: <see cref="Number"/> (with its row and section)
/// is the seat's IDENTITY - what gets printed on the ticket and read out at the door - while
/// <see cref="MapX"/>/<see cref="MapY"/> are GEOMETRY, only for drawing the picker. Conflating them
/// is why some platforms cannot move a seat two pixels without reissuing tickets.
/// </summary>
public class Seat
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid SeatRowId { get; set; }
    public SeatRow SeatRow { get; set; } = null!;

    /// <summary>Printed seat number - "12", "12A". Unique within its row.</summary>
    public required string Number { get; set; }

    public decimal? MapX { get; set; }
    public decimal? MapY { get; set; }

    /// <summary>
    /// Sellability is a property of the SEAT in this layout (a pillar blocks it, it is a wheelchair
    /// space, it is a house seat). Per-performance decisions - price, allocation, kills - belong to
    /// the performance, not here, which is why this is only a structural flag.
    /// </summary>
    public SeatKind Kind { get; set; } = SeatKind.Sellable;
}

public enum SeatKind
{
    Sellable,
    Accessible,     // wheelchair space or companion seat
    Restricted,     // sellable but with a restricted view - priced differently
    NotASeat        // aisle, pillar, table gap: present in the map, never sold
}
