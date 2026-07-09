namespace TicketingPlatform.Domain;

/// <summary>
/// A platform account. Three principals:
/// - Customer: platform-global buyer, no tenant.
/// - OrganizerStaff: back-office user scoped to exactly one tenant (TenantId set).
/// - PlatformAdmin: operates the platform itself, no tenant.
/// Users are deliberately NOT tenant-filtered: login happens before a tenant is known, and
/// customers/admins do not belong to a tenant at all. The tenant boundary for staff is enforced
/// by the tenant_id claim stamped into their JWT, not by a query filter on this table.
/// </summary>
public class User
{
    public Guid Id { get; set; }
    public required string Email { get; set; }

    /// <summary>Uppercased email for the unique index; lookups always normalize first.</summary>
    public required string NormalizedEmail { get; set; }

    /// <summary>PBKDF2 hash produced by ASP.NET Core Identity's PasswordHasher. Never a raw password.</summary>
    public required string PasswordHash { get; set; }

    public UserRole Role { get; set; }

    /// <summary>Set only for OrganizerStaff; null for Customer and PlatformAdmin.</summary>
    public Guid? TenantId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public enum UserRole
{
    Customer,
    OrganizerStaff,
    PlatformAdmin
}
