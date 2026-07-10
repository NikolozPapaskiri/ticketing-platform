using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TicketingPlatform.Application.Contracts;
using TicketingPlatform.Application.Services;

namespace TicketingPlatform.Api.Controllers;

/// <summary>
/// The GLOBAL public catalog (tkt.ge-style marketplace): every organizer's OnSale events in one
/// anonymous browse, with category / date / text / tenant filters. This is the buyer-facing
/// entry point; the per-tenant storefront (/public/tenants/{slug}/events) remains for
/// organizer-branded pages.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[AllowAnonymous]
[Route("api/v{version:apiVersion}/public/events")]
public sealed class MarketplaceController : ControllerBase
{
    private readonly EventService _events;

    public MarketplaceController(EventService events) => _events = events;

    [HttpGet]
    public async Task<ActionResult<PagedResponse<MarketplaceEventResponse>>> List(
        [FromQuery] string? category,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] string? q,
        [FromQuery] string? tenantSlug,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (page < 1)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid page number");

        pageSize = Math.Clamp(pageSize, 1, 100);
        var result = await _events.ListMarketplaceAsync(
            new MarketplaceFilter(category, from, to, q, tenantSlug), page, pageSize, ct);
        return result.IsSuccess ? Ok(result.Value) : NotFound();
    }

    /// <summary>Tenant-agnostic event detail: the marketplace's /events/{id} page.</summary>
    [HttpGet("{eventId:guid}")]
    public async Task<ActionResult<MarketplaceEventDetailResponse>> GetById(Guid eventId, CancellationToken ct)
    {
        var result = await _events.GetMarketplaceEventAsync(eventId, ct);
        return result.IsSuccess ? Ok(result.Value) : NotFound();
    }

    /// <summary>Event image for catalog cards. Anonymous; 404 when the event has no image.</summary>
    [HttpGet("{eventId:guid}/image")]
    [ResponseCache(Duration = 300)] // images are immutable-ish; let browsers cache them
    public async Task<IActionResult> GetImage(Guid eventId, CancellationToken ct)
    {
        var image = await _events.GetImageAsync(eventId, ct);
        return image is null
            ? NotFound()
            : File(image.Value.Stream, image.Value.ContentType);
    }
}
