using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TicketingPlatform.Application.Contracts;
using TicketingPlatform.Application.Services;

namespace TicketingPlatform.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[AllowAnonymous]
[Route("api/v{version:apiVersion}/public/tenants/{tenantSlug}/events")]
public sealed class PublicEventsController : ControllerBase
{
    private readonly EventService _events;

    public PublicEventsController(EventService events) => _events = events;

    [HttpGet]
    public async Task<ActionResult<PagedResponse<PublicEventListItemResponse>>> List(
        string tenantSlug,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (page < 1)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid page number");

        pageSize = Math.Clamp(pageSize, 1, 100);
        var result = await _events.ListPublicAsync(tenantSlug, page, pageSize, ct);
        return result.IsSuccess ? Ok(result.Value) : NotFound();
    }

    [HttpGet("{eventId:guid}")]
    public async Task<ActionResult<PublicEventResponse>> GetById(
        string tenantSlug, Guid eventId, CancellationToken ct)
    {
        var result = await _events.GetPublicByIdAsync(tenantSlug, eventId, ct);
        return result.IsSuccess ? Ok(result.Value) : NotFound();
    }
}
