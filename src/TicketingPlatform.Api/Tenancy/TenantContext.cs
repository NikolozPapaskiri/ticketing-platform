namespace TicketingPlatform.Api.Tenancy;

/// <summary>
/// Read-only view of the current tenant for a request. The DbContext depends on this to apply
/// its global query filter. Keeping the read contract separate from the setter means the rest
/// of the app cannot accidentally change the tenant mid-request.
/// </summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
    bool HasTenant { get; }
}

/// <summary>
/// Scoped (one per request). Populated by TenantResolutionMiddleware.
/// </summary>
public sealed class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }
    public bool HasTenant => TenantId.HasValue;

    public void SetTenant(Guid tenantId) => TenantId = tenantId;
}
