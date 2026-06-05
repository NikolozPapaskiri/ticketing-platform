namespace TicketingPlatform.Api.Tenancy;

/// <summary>
/// Resolves the current tenant from the X-Tenant-Id header and stores it in the scoped
/// TenantContext. In Phase 3 this is replaced by reading the tenant claim from the
/// authenticated principal, so clients can no longer choose their own tenant by header.
/// </summary>
public sealed class TenantResolutionMiddleware
{
    public const string TenantHeader = "X-Tenant-Id";

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
        if (context.Request.Headers.TryGetValue(TenantHeader, out var raw)
            && Guid.TryParse(raw, out var tenantId))
        {
            tenantContext.SetTenant(tenantId);
            _logger.LogDebug("Resolved tenant {TenantId}", tenantId);
        }

        await _next(context);
    }
}
