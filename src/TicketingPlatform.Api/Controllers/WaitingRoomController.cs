using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TicketingPlatform.Application.Common;
using TicketingPlatform.Application.Contracts;
using TicketingPlatform.Application.Services;

namespace TicketingPlatform.Api.Controllers;

/// <summary>
/// The public face of the virtual waiting room. Anonymous by design: the queue forms BEFORE
/// login/checkout, so the visitor is identified by a client-generated GUID, not a user id.
/// Join is idempotent (rejoining keeps your original spot) and GET is the polling fallback
/// for clients whose SignalR connection drops - losing the socket must not lose your place.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[AllowAnonymous]
[Route("api/v{version:apiVersion}/public/events/{eventId:guid}/queue")]
public sealed class WaitingRoomController : ControllerBase
{
    private readonly WaitingRoomService _waitingRoom;

    public WaitingRoomController(WaitingRoomService waitingRoom) => _waitingRoom = waitingRoom;

    [HttpPost]
    public async Task<ActionResult<QueueStatusResponse>> Join(Guid eventId, JoinQueueRequest request, CancellationToken ct)
    {
        if (request.VisitorId == Guid.Empty)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "visitorId is required");

        var clientKey = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var result = await _waitingRoom.JoinAsync(eventId, request.VisitorId, clientKey, ct);
        return result.Error switch
        {
            ResultError.None => Ok(result.Value),
            ResultError.Throttled => Problem(statusCode: StatusCodes.Status429TooManyRequests,
                title: "Too many queue joins", detail: result.Message),
            _ => NotFound()
        };
    }

    [HttpGet("{visitorId:guid}")]
    public async Task<ActionResult<QueueStatusResponse>> Status(Guid eventId, Guid visitorId, CancellationToken ct)
    {
        var result = await _waitingRoom.GetStatusAsync(eventId, visitorId, ct);
        return result.IsSuccess ? Ok(result.Value) : NotFound();
    }
}
