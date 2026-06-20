namespace TicketingPlatform.Application.Abstractions;

/// <summary>
/// Read-only view of the current tenant for a request. The DbContext depends on this to apply
/// its global query filter. This is a port: defined in Application, implemented in the Api layer
/// (which knows how to read the tenant from the request).
/// </summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
    bool HasTenant { get; }
}
