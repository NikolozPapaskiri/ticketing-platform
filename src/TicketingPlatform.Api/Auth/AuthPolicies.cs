namespace TicketingPlatform.Api.Auth;

/// <summary>
/// Named authorization policies. Policies over raw role checks: they compose (role AND claim),
/// live in one place, and controllers reference a name instead of re-encoding the rule.
/// </summary>
public static class AuthPolicies
{
    /// <summary>OrganizerStaff role AND a tenant_id claim - the claim is what scopes every query.</summary>
    public const string OrganizerStaff = nameof(OrganizerStaff);

    public const string PlatformAdmin = nameof(PlatformAdmin);
}
