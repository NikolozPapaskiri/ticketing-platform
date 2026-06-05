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
    public async Task<ActionResult<IReadOnlyList<EventListItemResponse>>> List(CancellationToken ct)
    {
        if (!_tenant.HasTenant)
        {
            return MissingTenant();
        }

        // Project the enum itself in SQL, then format it in memory. Calling Status.ToString()
        // inside the server-side projection is not reliably translatable to SQL.
        var rows = await _db.Events
            .AsNoTracking()
            .OrderBy(e => e.StartsAt)
            .Select(e => new { e.Id, e.Name, e.VenueName, e.StartsAt, e.Status })
            .ToListAsync(ct);

        var events = rows
            .Select(r => new EventListItemResponse(r.Id, r.Name, r.VenueName, r.StartsAt, r.Status.ToString()))
            .ToList();

        return Ok(events);
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

        if(!ev.CantransitionTo(target))
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
