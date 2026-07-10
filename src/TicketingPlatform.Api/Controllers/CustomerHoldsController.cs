using Asp.Versioning;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TicketingPlatform.Api.Tenancy;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Application.Common;
using TicketingPlatform.Application.Contracts;
using TicketingPlatform.Application.Services;
using TicketingPlatform.Domain;

namespace TicketingPlatform.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Authorize(Roles = nameof(UserRole.Customer))]
[Route("api/v{version:apiVersion}/customer/holds")]
public sealed class CustomerHoldsController : ControllerBase
{
    /// <summary>Anonymous browser identity for the waiting room (client-generated GUID).</summary>
    public const string VisitorHeader = "X-Visitor-Id";

    private readonly HoldService _holds;
    private readonly IHoldRepository _holdRepository;
    private readonly IWaitingRoom _waitingRoom;
    private readonly TenantContext _tenant;

    public CustomerHoldsController(HoldService holds, IHoldRepository holdRepository,
        IWaitingRoom waitingRoom, TenantContext tenant)
    {
        _holds = holds;
        _holdRepository = holdRepository;
        _waitingRoom = waitingRoom;
        _tenant = tenant;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<HoldResponse>>> List(CancellationToken ct) =>
        Ok(await _holds.ListForCustomerAsync(CurrentUserId(), ct));

    [HttpPost]
    public async Task<ActionResult<HoldResponse>> Create(CreateHoldRequest request, CancellationToken ct)
    {
        var context = await _holdRepository.GetTicketTypeSaleContextAsync(request.TicketTypeId, ct);
        if (context is null)
            return NotFound();

        if (context.EventStatus != EventStatus.OnSale.ToString())
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Event is not on sale",
                detail: "Customers can only reserve tickets for events that are on sale.");
        }

        // LOAD LEVELING: when the organizer armed the waiting room, only admitted visitors may
        // reserve. Enforced HERE, at the API, not in the UI - a curl user queues like everyone.
        if (context.WaitingRoomEnabled)
        {
            var isAdmitted = Guid.TryParse(Request.Headers[VisitorHeader], out var visitorId)
                && await _waitingRoom.IsAdmittedAsync(context.EventId, visitorId, ct);
            if (!isAdmitted)
            {
                return Problem(
                    statusCode: StatusCodes.Status429TooManyRequests,
                    title: "Waiting room is active",
                    detail: "This on-sale uses a waiting room. Join the queue and retry once admitted.");
            }
        }

        _tenant.SetTenant(context.TenantId);
        var result = await _holds.CreateAsync(context.TenantId, request, CurrentUserId(), ct);
        return result.Error switch
        {
            ResultError.None => CreatedAtAction(nameof(GetById), new { id = result.Value!.Id }, result.Value),
            ResultError.NotFound => NotFound(),
            _ => Problem(statusCode: StatusCodes.Status409Conflict, title: "Insufficient availability", detail: result.Message)
        };
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<HoldResponse>> GetById(Guid id, CancellationToken ct)
    {
        var tenantId = await _holdRepository.GetHoldTenantIdAsync(id, ct);
        if (tenantId is null)
            return NotFound();

        _tenant.SetTenant(tenantId.Value);
        var result = await _holds.GetForCustomerAsync(id, CurrentUserId(), ct);
        return result.IsSuccess ? Ok(result.Value) : NotFound();
    }

    private Guid CurrentUserId()
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? throw new InvalidOperationException("Authenticated customer is missing sub claim.");
        return Guid.Parse(sub);
    }
}
