using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using TicketingPlatform.Application.Contracts;
using TicketingPlatform.Application.Services;

namespace TicketingPlatform.Api.Controllers;

/// <summary>
/// Platform-admin operations on tenants. Tenant is not tenant-scoped, so these endpoints work
/// without an X-Tenant-Id header. In Phase 3 they are restricted to the platform-admin role.
/// Thin by design: delegates to TenantService and maps Results to HTTP. No EF here.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/tenants")]
public class TenantsController : ControllerBase
{
    private readonly TenantService _tenants;

    public TenantsController(TenantService tenants) => _tenants = tenants;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TenantResponse>>> List(CancellationToken ct) =>
        Ok(await _tenants.ListAsync(ct));

    [HttpPost]
    public async Task<ActionResult<TenantResponse>> Create(CreateTenantRequest request, CancellationToken ct)
    {
        var result = await _tenants.CreateAsync(request, ct);
        if (!result.IsSuccess)
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Slug already in use",
                detail: result.Message);
        }

        return CreatedAtAction(nameof(List), null, result.Value);
    }
}
