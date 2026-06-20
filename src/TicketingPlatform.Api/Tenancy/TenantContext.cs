using TicketingPlatform.Application.Abstractions;

namespace TicketingPlatform.Api.Tenancy;

/// <summary>
/// Scoped (one per request). Populated by TenantResolutionMiddleware. Implements the
/// Application-layer ITenantContext port; the DbContext reads it through that interface.
/// </summary>
public sealed class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }
    public bool HasTenant => TenantId.HasValue;

    public void SetTenant(Guid tenantId) => TenantId = tenantId;
}
