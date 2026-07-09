using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TicketingPlatform.Api.Auth;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Application.Common;
using TicketingPlatform.Application.Contracts;
using TicketingPlatform.Application.Services;

namespace TicketingPlatform.Api.Controllers;

/// <summary>
/// Tenant-scoped hold (TTL reservation) operations - the organizer back-office path for now
/// (box-office reservations); the customer purchase flow arrives with the Phase 5 booking saga.
/// Thin: HTTP guards + Result mapping only.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Authorize(Policy = AuthPolicies.OrganizerStaff)]
[Route("api/v{version:apiVersion}/holds")]
public class HoldsController : ControllerBase
{
    private readonly HoldService _holds;
    private readonly ITenantContext _tenant;

    public HoldsController(HoldService holds, ITenantContext tenant)
    {
        _holds = holds;
        _tenant = tenant;
    }

    [HttpPost]
    public async Task<ActionResult<HoldResponse>> Create(CreateHoldRequest request, CancellationToken ct)
    {
        if (!_tenant.HasTenant)
            return MissingTenant();

        var result = await _holds.CreateAsync(_tenant.TenantId!.Value, request, ct);
        return result.Error switch
        {
            ResultError.NotFound => NotFound(),
            ResultError.Conflict => Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Insufficient availability",
                detail: result.Message),
            _ => CreatedAtAction(nameof(GetById), new { id = result.Value!.Id }, result.Value)
        };
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<HoldResponse>> GetById(Guid id, CancellationToken ct)
    {
        if (!_tenant.HasTenant)
            return MissingTenant();

        var result = await _holds.GetByIdAsync(id, ct);
        return result.IsSuccess ? Ok(result.Value) : NotFound();
    }

    [HttpPost("{id:guid}/release")]
    public async Task<IActionResult> Release(Guid id, CancellationToken ct)
    {
        if (!_tenant.HasTenant)
            return MissingTenant();

        var result = await _holds.ReleaseAsync(id, ct);
        return result.Error switch
        {
            ResultError.None => NoContent(),
            ResultError.NotFound => NotFound(),
            _ => Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Hold cannot be released",
                detail: result.Message)
        };
    }

    // Defense-in-depth: the OrganizerStaff policy already requires a tenant claim.
    private ObjectResult MissingTenant() =>
        Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Missing tenant",
            detail: "This operation requires a token carrying a tenant claim (organizer staff).");
}
