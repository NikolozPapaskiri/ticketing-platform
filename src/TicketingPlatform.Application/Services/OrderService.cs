using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Application.Common;
using TicketingPlatform.Application.Contracts;
using TicketingPlatform.Domain;

namespace TicketingPlatform.Application.Services;

/// <summary>
/// The booking saga: hold -> charge -> confirm -> announce.
///
///   1. Validate the hold (Active, not past TTL) - the inventory is already reserved, so this
///      step never touches availability.
///   2. Charge the payment provider (resilient client; the ORDER ID is the idempotency key, so
///      a retried charge for the same order can never bill twice).
///   3a. Success: order Confirmed + hold Confirmed + OrderConfirmed event staged in the outbox,
///       ALL persisted by one SaveChanges = one transaction. The dispatcher publishes to
///       RabbitMQ afterwards - the dual-write problem is solved by the outbox, not by hope.
///   3b. Declined: order PaymentFailed; the hold STAYS ACTIVE so the buyer can retry with
///       another card until the TTL. Compensation is the hold-expiry service: if they never
///       succeed, the inventory flows back automatically. Nothing is ever manually unwound.
///   3c. Provider down: 503 to the caller, nothing recorded as failed - the buyer's hold is
///       untouched and they simply try again.
/// </summary>
public sealed class OrderService
{
    private readonly IOrderRepository _orders;
    private readonly IPaymentGateway _payments;
    private readonly IOutbox _outbox;
    private readonly TimeProvider _clock;

    public OrderService(IOrderRepository orders, IPaymentGateway payments, IOutbox outbox, TimeProvider clock)
    {
        _orders = orders;
        _payments = payments;
        _outbox = outbox;
        _clock = clock;
    }

    public async Task<Result<OrderResponse>> CreateAsync(Guid tenantId, CreateOrderRequest request, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        var hold = await _orders.GetHoldForOrderAsync(request.HoldId, ct);
        if (hold is null)
            return Result<OrderResponse>.NotFound($"Hold '{request.HoldId}' was not found.");

        if (hold.Status != HoldStatus.Active || hold.IsExpired(now))
            return Result<OrderResponse>.Conflict($"Hold is '{(hold.IsExpired(now) ? "expired" : hold.Status.ToString())}' and cannot be purchased.");

        var order = new Order
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            HoldId = hold.Id,
            CustomerEmail = request.CustomerEmail,
            Amount = hold.TicketType.Price * hold.Quantity,
            Currency = hold.TicketType.Currency,
            CreatedAt = now
        };

        // The order id doubles as the idempotency key: however many times the resilient client
        // retries this charge, the provider counts it once.
        var payment = await _payments.ChargeAsync(
            new PaymentCharge(order.Id.ToString(), order.Amount, order.Currency), ct);

        if (payment.Failure == PaymentFailure.ProviderUnavailable)
        {
            // Do not persist a failed order for an outage - the buyer just retries later.
            return Result<OrderResponse>.Unavailable("The payment provider is unavailable; please try again shortly.");
        }

        _orders.Add(order);

        if (!payment.Succeeded)
        {
            // Declined: record the failed attempt; the hold survives for a retry until TTL.
            order.MarkPaymentFailed();
            await _orders.SaveChangesAsync(ct);
            return Result<OrderResponse>.Conflict("Payment was declined.");
        }

        order.MarkConfirmed(payment.ProviderChargeId!);
        hold.Confirm(); // the reserved quantity is now permanently sold

        // Staged into the SAME transaction as the order + hold changes (the outbox pattern).
        _outbox.Add("OrderConfirmed", new
        {
            OrderId = order.Id,
            TenantId = tenantId,
            TicketTypeId = hold.TicketTypeId,
            hold.Quantity,
            order.CustomerEmail,
            order.Amount,
            order.Currency
        });

        await _orders.SaveChangesAsync(ct);

        return Result<OrderResponse>.Success(Map(order));
    }

    public async Task<Result<OrderResponse>> GetByIdAsync(Guid orderId, CancellationToken ct)
    {
        var order = await _orders.GetAsync(orderId, ct);
        return order is null
            ? Result<OrderResponse>.NotFound($"Order '{orderId}' was not found.")
            : Result<OrderResponse>.Success(Map(order));
    }

    private static OrderResponse Map(Order order) =>
        new(order.Id, order.HoldId, order.CustomerEmail, order.Amount, order.Currency, order.Status.ToString());
}
