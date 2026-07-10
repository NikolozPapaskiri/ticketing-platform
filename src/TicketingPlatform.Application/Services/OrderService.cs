using System.Security.Cryptography;
using System.Text;
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
    private readonly IReservationStrategy _reservation;
    private readonly IOutbox _outbox;
    private readonly TimeProvider _clock;

    public OrderService(IOrderRepository orders, IPaymentGateway payments,
        IReservationStrategy reservation, IOutbox outbox, TimeProvider clock)
    {
        _orders = orders;
        _payments = payments;
        _reservation = reservation;
        _outbox = outbox;
        _clock = clock;
    }

    public async Task<Result<OrderResponse>> CreateAsync(Guid tenantId, CreateOrderRequest request, CancellationToken ct)
        => await CreateAsync(tenantId, request, customerUserId: null, idempotencyKey: null, actorKey: null, ct);

    public async Task<Result<OrderResponse>> CreateAsync(
        Guid tenantId,
        CreateOrderRequest request,
        Guid? customerUserId,
        string? idempotencyKey,
        string? actorKey,
        CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        idempotencyKey = NormalizeKey(idempotencyKey);
        actorKey ??= customerUserId is null ? $"tenant:{tenantId:N}" : $"customer:{customerUserId.Value:N}";
        var requestHash = HashRequest(request, customerUserId);
        IdempotencyRecord? idempotency = null;
        var orderId = Guid.NewGuid();

        if (idempotencyKey is not null)
        {
            idempotency = await _orders.GetIdempotencyRecordAsync(tenantId, actorKey, idempotencyKey, ct);
            if (idempotency is not null)
            {
                if (!StringComparer.Ordinal.Equals(idempotency.RequestHash, requestHash))
                    return Result<OrderResponse>.Conflict("This idempotency key was already used for a different order request.");

                if (idempotency.Status == IdempotencyRecordStatus.InProgress)
                    return Result<OrderResponse>.Conflict("An order with this idempotency key is still in progress.");

                var existing = await _orders.GetAsync(idempotency.OrderId, ct);
                return existing is null
                    ? Result<OrderResponse>.Conflict("The idempotency record points to an order that no longer exists.")
                    : Result<OrderResponse>.Success(Map(existing));
            }
        }

        var hold = await _orders.GetHoldForOrderAsync(request.HoldId, ct);
        if (hold is null)
            return Result<OrderResponse>.NotFound($"Hold '{request.HoldId}' was not found.");

        if (hold.Status != HoldStatus.Active || hold.IsExpired(now))
            return Result<OrderResponse>.Conflict($"Hold is '{(hold.IsExpired(now) ? "expired" : hold.Status.ToString())}' and cannot be purchased.");

        if (customerUserId is not null && hold.CustomerUserId != customerUserId)
            return Result<OrderResponse>.NotFound($"Hold '{request.HoldId}' was not found.");

        if (idempotencyKey is not null)
        {
            idempotency = new IdempotencyRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ActorKey = actorKey,
                Key = idempotencyKey,
                RequestHash = requestHash,
                OrderId = orderId,
                CreatedAt = now
            };
            _orders.Add(idempotency);
            await _orders.SaveChangesAsync(ct); // claim the key before the external charge
        }

        var order = new Order
        {
            Id = orderId,
            TenantId = tenantId,
            HoldId = hold.Id,
            CustomerUserId = customerUserId,
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
            if (idempotency is not null)
            {
                _orders.Remove(idempotency);
                await _orders.SaveChangesAsync(ct);
            }
            TicketingMetrics.OrdersPaymentUnavailable.Add(1);
            return Result<OrderResponse>.Unavailable("The payment provider is unavailable; please try again shortly.");
        }

        _orders.Add(order);

        if (!payment.Succeeded)
        {
            // Declined: record the failed attempt; the hold survives for a retry until TTL.
            order.MarkPaymentFailed();
            idempotency?.Complete(now);
            await _orders.SaveChangesAsync(ct);
            TicketingMetrics.OrdersPaymentDeclined.Add(1);
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

        idempotency?.Complete(now);
        await _orders.SaveChangesAsync(ct);

        TicketingMetrics.OrdersConfirmed.Add(1);
        return Result<OrderResponse>.Success(Map(order));
    }

    public async Task<Result<OrderResponse>> GetByIdAsync(Guid orderId, CancellationToken ct)
    {
        var order = await _orders.GetAsync(orderId, ct);
        return order is null
            ? Result<OrderResponse>.NotFound($"Order '{orderId}' was not found.")
            : Result<OrderResponse>.Success(Map(order));
    }

    public async Task<IReadOnlyList<OrderResponse>> ListForCustomerAsync(Guid customerUserId, CancellationToken ct)
    {
        var orders = await _orders.ListForCustomerAsync(customerUserId, ct);
        return orders.Select(Map).ToList();
    }

    public async Task<Result<OrderResponse>> GetForCustomerAsync(Guid orderId, Guid customerUserId, CancellationToken ct)
    {
        var order = await _orders.GetForCustomerAsync(orderId, customerUserId, ct);
        return order is null
            ? Result<OrderResponse>.NotFound($"Order '{orderId}' was not found.")
            : Result<OrderResponse>.Success(Map(order));
    }

    public async Task<Result<OrderResponse>> RefundAsync(Guid tenantId, Guid orderId, string actorKey, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var order = await _orders.GetForRefundAsync(orderId, ct);
        if (order is null)
            return Result<OrderResponse>.NotFound($"Order '{orderId}' was not found.");

        if (order.Status != OrderStatus.Confirmed)
            return Result<OrderResponse>.Conflict($"Order is '{order.Status}' and cannot be refunded.");

        if (order.ProviderChargeId is null)
            return Result<OrderResponse>.Conflict("Order has no provider charge to refund.");

        var refund = await _payments.RefundAsync(
            new PaymentRefund($"refund:{order.Id:N}:{actorKey}", order.ProviderChargeId, order.Amount, order.Currency), ct);

        if (refund.Failure == PaymentFailure.ProviderUnavailable)
        {
            TicketingMetrics.OrdersPaymentUnavailable.Add(1, new KeyValuePair<string, object?>("operation", "refund"));
            return Result<OrderResponse>.Unavailable("The payment provider is unavailable; please try again shortly.");
        }

        if (!refund.Succeeded)
            return Result<OrderResponse>.Conflict("Refund was declined.");

        order.MarkRefunded(refund.ProviderChargeId!, now);

        var ticket = await _orders.GetTicketForUpdateAsync(order.Id, ct);
        ticket?.Void(now);

        _outbox.Add("AvailabilityChanged", new
        {
            TenantId = tenantId,
            order.Hold.TicketType.EventId,
            order.Hold.TicketTypeId
        });
        _outbox.Add("OrderRefunded", new
        {
            OrderId = order.Id,
            TenantId = tenantId,
            order.CustomerEmail,
            order.Amount,
            order.Currency
        });

        await _reservation.ReleaseAsync(order.Hold, ct);
        TicketingMetrics.OrdersRefunded.Add(1);
        return Result<OrderResponse>.Success(Map(order));
    }

    private static OrderResponse Map(Order order) =>
        new(order.Id, order.HoldId, order.CustomerEmail, order.Amount, order.Currency, order.Status.ToString());

    private static string? NormalizeKey(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : key.Trim();

    private static string HashRequest(CreateOrderRequest request, Guid? customerUserId)
    {
        var canonical = $"{request.HoldId:N}|{request.CustomerEmail.Trim().ToUpperInvariant()}|{customerUserId?.ToString("N") ?? "staff"}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
