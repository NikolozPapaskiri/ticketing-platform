using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TicketingPlatform.Api.Auth;
using TicketingPlatform.Api.Features.Ops;

namespace TicketingPlatform.Api.Controllers;

/// <summary>
/// Read-only operations snapshot for the in-app admin ops page. PlatformAdmin only - it exposes
/// platform-wide (cross-tenant) health and backlog figures.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/admin/ops")]
[Authorize(Policy = AuthPolicies.PlatformAdmin)]
public class AdminOpsController : ControllerBase
{
    private readonly OpsSnapshotService _ops;
    public AdminOpsController(OpsSnapshotService ops) => _ops = ops;

    [HttpGet]
    public async Task<ActionResult<OpsSnapshot>> Get(CancellationToken ct) => Ok(await _ops.GetAsync(ct));
}
