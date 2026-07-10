using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Asp.Versioning;
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
[Route("api/v{version:apiVersion}/customer/orders")]
public sealed class CustomerOrdersController : ControllerBase
{
    private const string IdempotencyHeader = "Idempotency-Key";

    private readonly OrderService _orders;
    private readonly IOrderRepository _orderRepository;
    private readonly IFileStorage _files;
    private readonly TenantContext _tenant;

    public CustomerOrdersController(OrderService orders, IOrderRepository orderRepository,
        IFileStorage files, TenantContext tenant)
    {
        _orders = orders;
        _orderRepository = orderRepository;
        _files = files;
        _tenant = tenant;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<OrderResponse>>> List(CancellationToken ct) =>
        Ok(await _orders.ListForCustomerAsync(CurrentUserId(), ct));

    [HttpPost]
    public async Task<ActionResult<OrderResponse>> Create(CreateCustomerOrderRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId();
        var email = User.FindFirstValue(JwtRegisteredClaimNames.Email) ?? User.FindFirstValue(ClaimTypes.Email);
        if (email is null)
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Missing email claim");

        var tenantId = await _orderRepository.GetHoldTenantIdAsync(request.HoldId, ct);
        if (tenantId is null)
            return NotFound();

        _tenant.SetTenant(tenantId.Value);
        var result = await _orders.CreateAsync(
            tenantId.Value,
            new CreateOrderRequest(request.HoldId, email),
            userId,
            Request.Headers[IdempotencyHeader].FirstOrDefault(),
            $"customer:{userId:N}",
            ct);

        return result.Error switch
        {
            ResultError.None => CreatedAtAction(nameof(GetById), new { id = result.Value!.Id }, result.Value),
            ResultError.NotFound => NotFound(),
            ResultError.Unavailable => Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Payment provider unavailable", detail: result.Message),
            _ => Problem(statusCode: StatusCodes.Status409Conflict, title: "Order cannot be completed", detail: result.Message)
        };
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<OrderResponse>> GetById(Guid id, CancellationToken ct)
    {
        var result = await _orders.GetForCustomerAsync(id, CurrentUserId(), ct);
        return result.IsSuccess ? Ok(result.Value) : NotFound();
    }

    [HttpPost("{id:guid}/refund")]
    public async Task<ActionResult<OrderResponse>> Refund(Guid id, CancellationToken ct)
    {
        var userId = CurrentUserId();
        var order = await _orderRepository.GetForCustomerAsync(id, userId, ct);
        if (order is null)
            return NotFound();

        _tenant.SetTenant(order.TenantId);
        var result = await _orders.RefundAsync(order.TenantId, id, $"customer:{userId:N}", ct);
        return result.Error switch
        {
            ResultError.None => Ok(result.Value),
            ResultError.NotFound => NotFound(),
            ResultError.Unavailable => Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Payment provider unavailable", detail: result.Message),
            _ => Problem(statusCode: StatusCodes.Status409Conflict, title: "Order cannot be refunded", detail: result.Message)
        };
    }

    [HttpGet("{id:guid}/ticket")]
    public async Task<IActionResult> DownloadTicket(Guid id, CancellationToken ct)
    {
        var ticket = await _orderRepository.GetTicketForCustomerAsync(id, CurrentUserId(), ct);
        if (ticket is null)
            return NotFound();

        var stream = await _files.OpenReadAsync(ticket.FilePath, ct);
        return stream is null ? NotFound() : File(stream, "application/pdf", $"ticket-{id}.pdf");
    }

    private Guid CurrentUserId()
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? throw new InvalidOperationException("Authenticated customer is missing sub claim.");
        return Guid.Parse(sub);
    }
}
