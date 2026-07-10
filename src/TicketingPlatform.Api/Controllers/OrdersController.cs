using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
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
    private const string IdempotencyHeader = "Idempotency-Key";

    private readonly OrderService _orders;
    private readonly IOrderRepository _orderRepository;
    private readonly IFileStorage _files;
    private readonly ITenantContext _tenant;

    public OrdersController(OrderService orders, IOrderRepository orderRepository,
        IFileStorage files, ITenantContext tenant)
    {
        _orders = orders;
        _orderRepository = orderRepository;
        _files = files;
        _tenant = tenant;
    }

    /// <summary>
    /// Downloads the issued ticket PDF. The document is produced ASYNCHRONOUSLY by the
    /// ticket-issuer consumer after OrderConfirmed, so a 404 right after checkout just means
    /// "not issued yet" - clients retry. The lookup is tenant-scoped (query filter), so a
    /// foreign tenant's order id yields 404 like everywhere else.
    /// </summary>
    [HttpGet("{id:guid}/ticket")]
    public async Task<IActionResult> DownloadTicket(Guid id, CancellationToken ct)
    {
        var ticket = await _orderRepository.GetTicketAsync(id, ct);
        if (ticket is null)
            return NotFound();

        var stream = await _files.OpenReadAsync(ticket.FilePath, ct);
        if (stream is null)
            return NotFound(); // metadata without a blob: storage lost the file

        return File(stream, "application/pdf", $"ticket-{id}.pdf");
    }

    [HttpPost]
    public async Task<ActionResult<OrderResponse>> Create(CreateOrderRequest request, CancellationToken ct)
    {
        var actorKey = $"staff:{CurrentUserId():N}";
        var result = await _orders.CreateAsync(
            _tenant.TenantId!.Value,
            request,
            customerUserId: null,
            Request.Headers[IdempotencyHeader].FirstOrDefault(),
            actorKey,
            ct);
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

    [HttpPost("{id:guid}/refund")]
    public async Task<ActionResult<OrderResponse>> Refund(Guid id, CancellationToken ct)
    {
        var result = await _orders.RefundAsync(_tenant.TenantId!.Value, id, $"staff:{CurrentUserId():N}", ct);
        return result.Error switch
        {
            ResultError.None => Ok(result.Value),
            ResultError.NotFound => NotFound(),
            ResultError.Unavailable => Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Payment provider unavailable",
                detail: result.Message),
            _ => Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Order cannot be refunded",
                detail: result.Message)
        };
    }

    private Guid CurrentUserId()
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? throw new InvalidOperationException("Authenticated staff token is missing sub claim.");
        return Guid.Parse(sub);
    }
}
