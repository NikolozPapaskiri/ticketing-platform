using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TicketingPlatform.Api.Auth;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Application.Contracts;
using TicketingPlatform.Application.Services;
using TicketingPlatform.Domain;

namespace TicketingPlatform.Api.Controllers;

/// <summary>
/// Tenant-scoped event operations, restricted to organizer staff. The tenant comes from the
/// caller's signed tenant_id claim (resolved by TenantResolutionMiddleware) - never from the
/// client directly. Thin by design: HTTP guards + Result-to-status mapping live here;
/// querying, the state machine, and mapping live in EventService.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Authorize(Policy = AuthPolicies.OrganizerStaff)]
[Route("api/v{version:apiVersion}/events")]
public class EventsController : ControllerBase
{
    private readonly EventService _events;
    private readonly IAvailabilityReadModel _availability;
    private readonly ITenantContext _tenant;
    private readonly ILogger<EventsController> _logger;

    public EventsController(EventService events, IAvailabilityReadModel availability,
        ITenantContext tenant, ILogger<EventsController> logger)
    {
        _events = events;
        _availability = availability;
        _tenant = tenant;
        _logger = logger;
    }

    /// <summary>
    /// Availability from the CQRS read model: browse traffic reads this denormalized table,
    /// never the contested Inventories row. Eventually consistent (updated when the
    /// AvailabilityChanged event flows through the broker) - which is exactly the trade the
    /// pattern makes: staleness measured in milliseconds for reads that cost nothing.
    /// </summary>
    [HttpGet("{id:guid}/availability")]
    public async Task<ActionResult<IReadOnlyList<TicketAvailabilityResponse>>> GetAvailability(Guid id, CancellationToken ct)
    {
        if (!_tenant.HasTenant)
            return MissingTenant();

        return Ok(await _availability.GetForEventAsync(id, ct));
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

        var result = await _events.GetByIdAsync(_tenant.TenantId!.Value, id, ct);
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

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<EventResponse>> Update(Guid id, UpdateEventRequest request, CancellationToken ct)
    {
        if (!_tenant.HasTenant)
            return MissingTenant();

        var result = await _events.UpdateAsync(_tenant.TenantId!.Value, id, request, ct);
        return result.IsSuccess ? Ok(result.Value) : NotFound();
    }

    /// <summary>
    /// Uploads the event's marketplace image (JPEG/PNG/WebP, max 2 MB). Stored through the
    /// IFileStorage port; served anonymously at /public/events/{id}/image for catalog cards.
    /// </summary>
    [HttpPost("{id:guid}/image")]
    public async Task<IActionResult> UploadImage(Guid id, IFormFile file, CancellationToken ct)
    {
        if (!_tenant.HasTenant)
            return MissingTenant();

        if (file.Length is 0 or > Application.Services.EventService.MaxImageBytes)
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid image", detail: "Image must be between 1 byte and 2 MB.");

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);

        var result = await _events.SetImageAsync(_tenant.TenantId!.Value, id, buffer.ToArray(), file.ContentType, ct);
        return result.Error switch
        {
            Application.Common.ResultError.None => NoContent(),
            Application.Common.ResultError.NotFound => NotFound(),
            _ => Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid image", detail: result.Message)
        };
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

        var result = await _events.TransitionAsync(_tenant.TenantId!.Value, id, target, ct);
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

    // Defense-in-depth: the OrganizerStaff policy already requires a tenant claim, so this
    // guard should be unreachable. It stays so a future policy change cannot silently produce
    // tenant-less queries against tenant-filtered tables.
    private ObjectResult MissingTenant() =>
        Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Missing tenant",
            detail: "This operation requires a token carrying a tenant claim (organizer staff).");
}
