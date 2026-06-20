namespace TicketingPlatform.Api.Domain;

/// <summary>
/// An event organizer. This is the tenant boundary. Tenant itself is not tenant-scoped:
/// it has no TenantId and no global query filter, so platform-admin operations can list and
/// create tenants without a tenant context.
/// </summary>
public class Tenant
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<Event> Events { get; set; } = new List<Event>();
}
