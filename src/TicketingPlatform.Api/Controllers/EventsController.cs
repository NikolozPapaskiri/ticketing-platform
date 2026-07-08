using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using TicketingPlatform.Api.Tenancy;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Application.Contracts;
using TicketingPlatform.Application.Services;
using TicketingPlatform.Domain;

namespace TicketingPlatform.Api.Controllers;

/// <summary>
/// Tenant-scoped event operations. Thin by design: HTTP-shaped guards (missing tenant, page
/// parameter validation) and Result-to-status mapping live here; querying, the state machine,
/// and mapping live in EventService. Tenant isolation is enforced by the EF global query filter
/// inside Infrastructure — no hand-written tenant predicate anywhere.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/events")]
public class EventsController : ControllerBase
{
    private readonly EventService _events;
    private readonly ITenantContext _tenant;
    private readonly ILogger<EventsController> _logger;

    public EventsController(EventService events, ITenantContext tenant, ILogger<EventsController> logger)
    {
        _events = events;
        _tenant = tenant;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResponse<EventListItemResponse>>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] EventStatus? status = null,
        CancellationToken ct = default)
    {
        if (!_tenant.HasTenant)
            return MissingTenant();

        // page: reject bad values (400). pageSize: clamp silently. Both are guardrails —
        // without the clamp, ?pageSize=1000000 pulls the whole table.
        if (page < 1)
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid page number",
                detail: "Page number must be greater than or equal to 1.");

        pageSize = Math.Clamp(pageSize, 1, 100);

        return Ok(await _events.ListAsync(page, pageSize, status, ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<EventResponse>> GetById(Guid id, CancellationToken ct)
    {
        if (!_tenant.HasTenant)
            return MissingTenant();

        var result = await _events.GetByIdAsync(id, ct);
        return result.IsSuccess ? Ok(result.Value) : NotFound();
    }

    [HttpPost]
    public async Task<ActionResult<EventResponse>> Create(CreateEventRequest request, CancellationToken ct)
    {
        if (!_tenant.HasTenant)
            return MissingTenant();

        var response = await _events.CreateAsync(_tenant.TenantId!.Value, request, ct);

        _logger.LogInformation("Created event {EventId} for tenant {TenantId}", response.Id, _tenant.TenantId);

        return CreatedAtAction(nameof(GetById), new { id = response.Id }, response);
    }

    [HttpPost("{eventId:guid}/ticket-types")]
    public async Task<ActionResult<TicketTypeResponse>> AddTicketType(
        Guid eventId,
        CreateTicketTypeRequest request,
        CancellationToken ct)
    {
        if (!_tenant.HasTenant)
            return MissingTenant();

        var result = await _events.AddTicketTypeAsync(_tenant.TenantId!.Value, eventId, request, ct);
        return result.IsSuccess ? Ok(result.Value) : NotFound();
    }

    [HttpPost("{id:guid}/publish")]   // Draft -> OnSale
    public Task<IActionResult> Publish(Guid id, CancellationToken ct)
        => Transition(id, EventStatus.OnSale, ct);

    [HttpPost("{id:guid}/close")]     // Draft/OnSale -> Closed
    public Task<IActionResult> Close(Guid id, CancellationToken ct)
        => Transition(id, EventStatus.Closed, ct);

    private async Task<IActionResult> Transition(Guid id, EventStatus target, CancellationToken ct)
    {
        if (!_tenant.HasTenant)
            return MissingTenant();

        var result = await _events.TransitionAsync(id, target, ct);
        if (result.IsSuccess)
        {
            _logger.LogInformation("Transitioned event {EventId} to {Status}", id, target);
            return NoContent();
        }

        return result.Error switch
        {
            Application.Common.ResultError.NotFound => NotFound(),
            _ => Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Illegal status transition",
                detail: result.Message)
        };
    }

    private ObjectResult MissingTenant() =>
        Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Missing tenant",
            detail: $"The '{TenantResolutionMiddleware.TenantHeader}' header is required for this operation.");
}
