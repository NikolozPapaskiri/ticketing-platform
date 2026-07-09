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
/// The booking saga's entry point (box-office flow: staff sells to a walk-up customer).
/// Thin: OrderService owns hold validation, the charge, confirmation, and the outbox event.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Authorize(Policy = AuthPolicies.OrganizerStaff)]
[Route("api/v{version:apiVersion}/orders")]
public class OrdersController : ControllerBase
{
    private readonly OrderService _orders;
    private readonly ITenantContext _tenant;

    public OrdersController(OrderService orders, ITenantContext tenant)
    {
        _orders = orders;
        _tenant = tenant;
    }

    [HttpPost]
    public async Task<ActionResult<OrderResponse>> Create(CreateOrderRequest request, CancellationToken ct)
    {
        var result = await _orders.CreateAsync(_tenant.TenantId!.Value, request, ct);
        return result.Error switch
        {
            ResultError.None => CreatedAtAction(nameof(GetById), new { id = result.Value!.Id }, result.Value),
            ResultError.NotFound => NotFound(),
            ResultError.Unavailable => Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Payment provider unavailable",
                detail: result.Message),
            _ => Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Order cannot be completed",
                detail: result.Message)
        };
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<OrderResponse>> GetById(Guid id, CancellationToken ct)
    {
        var result = await _orders.GetByIdAsync(id, ct);
        return result.IsSuccess ? Ok(result.Value) : NotFound();
    }
}
