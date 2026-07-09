using System.Security.Claims;

namespace TicketingPlatform.Api.Tenancy;

/// <summary>
/// Resolves the current tenant from the authenticated principal's tenant_id claim and stores
/// it in the scoped TenantContext.
/// Phase 3 replaced the old X-Tenant-Id header: the claim is inside a server-signed JWT, so a
/// client can no longer choose its own tenant - forging a tenant now requires forging a
/// signature. Must run AFTER UseAuthentication (the principal has to exist) and BEFORE
/// controllers (the DbContext query filter reads the result).
/// </summary>
public sealed class TenantResolutionMiddleware
{
    public const string TenantClaim = "tenant_id";

    private readonly RequestDelegate _next;
    private readonly ILogger<TenantResolutionMiddleware> _logger;

    public TenantResolutionMiddleware(RequestDelegate next, ILogger<TenantResolutionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    // TenantContext is injected per request because middleware InvokeAsync resolves scoped services.
    public async Task InvokeAsync(HttpContext context, TenantContext tenantContext)
    {
        var value = context.User.FindFirstValue(TenantClaim);
        if (value is not null && Guid.TryParse(value, out var tenantId))
        {
            tenantContext.SetTenant(tenantId);
            _logger.LogDebug("Resolved tenant {TenantId} from token claim", tenantId);
        }

        await _next(context);
    }
}
