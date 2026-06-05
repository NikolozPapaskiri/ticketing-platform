using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TicketingPlatform.Api.Contracts;
using TicketingPlatform.Api.Data;
using TicketingPlatform.Api.Domain;
using TicketingPlatform.Api.Tenancy;

namespace TicketingPlatform.Api.Controllers;

/// <summary>
/// Tenant-scoped event operations. Reads are filtered to the current tenant automatically by the
/// DbContext global query filter; writes stamp the current tenant id. Note there is no hand-written
/// "WHERE TenantId = ..." anywhere in this controller. That is the filter doing its job.
/// </summary>
[ApiController]
[Route("api/events")]
public class EventsController : ControllerBase
{
    private readonly TicketingDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ILogger<EventsController> _logger;

    public EventsController(TicketingDbContext db, ITenantContext tenant, ILogger<EventsController> logger)
    {
        _db = db;
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

        // page: reject bad values (400). pageSize: clamp silently. Both are senior guardrails —
        // without the clamp, ?pageSize=1000000 pulls the whole table again.
        if(page < 1)
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid page number",
                detail: "Page number must more or equel to 1.");

        pageSize = Math.Clamp(pageSize, 1, 100);

        // Composable IQueryable: nothing executes yet. The tenant query filter is already baked in.
        var query = _db.Events.AsNoTracking();

        // Conditional chaining — the .Where only joins the expression tree when a filter is present.
        // e.Status == enum DOES translate to SQL because of HasConversion<string>() in the DbContext.
        if(status is not null)
            query = query.Where(e => e.Status == status);

        // Query 1: count of the *filtered* set (must come after the Where, before Skip/Take).
        var totalCount = await query.CountAsync(ct);

        // Query 2: the page itself. Stable order needs the Id tiebreaker — offset paging over a
        // non-unique key (StartsAt) is non-deterministic; two events at the same time could swap pages.
        var rows = await query
            .OrderBy(e => e.StartsAt)
            .ThenBy(e => e.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new { e.Id, e.Name, e.VenueName, e.StartsAt, e.Status })
            .ToListAsync(ct);

        // Map in memory — Status.ToString() is not reliably translatable inside the SQL projection,
        // which is why the Select above projects the enum and we format it here.
        var items = rows
            .Select(r => new EventListItemResponse(r.Id, r.Name, r.VenueName, r.StartsAt, r.Status.ToString()))
            .ToList();

        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);

        return Ok(new PagedResponse<EventListItemResponse>(items, page, pageSize, totalCount, totalPages));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<EventResponse>> GetById(Guid id, CancellationToken ct)
    {
        if (!_tenant.HasTenant)
        {
            return MissingTenant();
        }

        // Load the graph, then map in memory. This keeps Status.ToString() and the nested mapping
        // off the SQL side, where enum.ToString() is not reliably translatable.
        var ev = await _db.Events
            .AsNoTracking()
            .Include(e => e.TicketTypes)
                .ThenInclude(tt => tt.Inventory)
            .FirstOrDefaultAsync(e => e.Id == id, ct);

        if (ev is null)
        {
            return NotFound();
        }

        var response = new EventResponse(
            ev.Id,
            ev.Name,
            ev.Description,
            ev.VenueName,
            ev.StartsAt,
            ev.Status.ToString(),
            ev.TicketTypes
                .Select(tt => new TicketTypeResponse(
                    tt.Id,
                    tt.Name,
                    tt.Price,
                    tt.Currency,
                    tt.Inventory.TotalQuantity,
                    tt.Inventory.AvailableQuantity))
                .ToList());

        return Ok(response);
    }

    [HttpPost]
    public async Task<ActionResult<EventResponse>> Create(CreateEventRequest request, CancellationToken ct)
    {
        if (!_tenant.HasTenant)
        {
            return MissingTenant();
        }

        var ev = new Event
        {
            Id = Guid.NewGuid(),
            TenantId = _tenant.TenantId!.Value,
            Name = request.Name,
            Description = request.Description,
            VenueName = request.VenueName,
            StartsAt = request.StartsAt,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.Events.Add(ev);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Created event {EventId} for tenant {TenantId}", ev.Id, ev.TenantId);

        var response = new EventResponse(
            ev.Id, ev.Name, ev.Description, ev.VenueName, ev.StartsAt, ev.Status.ToString(), []);

        return CreatedAtAction(nameof(GetById), new { id = ev.Id }, response);
    }

    [HttpPost("{eventId:guid}/ticket-types")]
    public async Task<ActionResult<TicketTypeResponse>> AddTicketType(
        Guid eventId,
        CreateTicketTypeRequest request,
        CancellationToken ct)
    {
        if (!_tenant.HasTenant)
        {
            return MissingTenant();
        }

        // The query is tenant-scoped, so an event from another tenant is invisible and returns 404.
        var eventExists = await _db.Events.AnyAsync(e => e.Id == eventId, ct);
        if (!eventExists)
        {
            return NotFound();
        }

        var tenantId = _tenant.TenantId!.Value;
        var ticketType = new TicketType
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            EventId = eventId,
            Name = request.Name,
            Price = request.Price,
            Currency = request.Currency,
            Inventory = new Inventory
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                TotalQuantity = request.TotalQuantity,
                AvailableQuantity = request.TotalQuantity
            }
        };

        _db.TicketTypes.Add(ticketType);
        await _db.SaveChangesAsync(ct);

        var response = new TicketTypeResponse(
            ticketType.Id,
            ticketType.Name,
            ticketType.Price,
            ticketType.Currency,
            ticketType.Inventory.TotalQuantity,
            ticketType.Inventory.AvailableQuantity);

        return Ok(response);
    }

    private ObjectResult MissingTenant() =>
        Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Missing tenant",
            detail: $"The '{TenantResolutionMiddleware.TenantHeader}' header is required for this operation.");

    [HttpPost("{id:guid}/publish")]   // Draft -> OnSale
    public Task<IActionResult> Publish(Guid id, CancellationToken ct)
        => Transition(id, EventStatus.OnSale, ct);

    [HttpPost("{id:guid}/close")]     // Draft/OnSale -> Closed
    public Task<IActionResult> Close(Guid id, CancellationToken ct)
        => Transition(id, EventStatus.Closed, ct);

    private async Task<IActionResult> Transition(Guid id, EventStatus target, CancellationToken ct)
    {
        if(!_tenant.HasTenant)
            return MissingTenant();

        // FirstOrDefaultAsync (a LINQ query) honors the tenant global query filter, so a tenant
        // can't transition another tenant's event. It's also a *tracked* query, so the change saves.
        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == id, ct);
        if(ev is null)
            return NotFound();

        if(!ev.CanTransitionTo(target))
            return Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Illegal status transition",
                Detail = $"An event in '{ev.Status}' cannot move to '{target}'."
            });

        ev.TransitionTo(target);              // safe: pre-checked, won't throw
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Transitioned event {EventId} to {Status}", ev.Id, target);
        return NoContent();
    }
}
