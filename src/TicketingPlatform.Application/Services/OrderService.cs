using System.Security.Cryptography;
using System.Text;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Application.Common;
using TicketingPlatform.Application.Contracts;
using TicketingPlatform.Domain;

namespace TicketingPlatform.Application.Services;

/// <summary>
/// The booking saga, rebuilt as a DURABLE payment state machine so no failure window can
/// oversell a seat or lose money (see docs/PRODUCTION_SAFETY_HARDENING_PLAN.md, PR 1).
///
///   1. Replay/recover: if the idempotency key already has an order, return it - or, if that
///      order is still PendingPayment (a crashed/lost attempt), reconcile it with the provider.
///   2. Claim: atomically flip the hold Active -> PaymentPending (concurrency-token guarded, so
///      exactly one checkout wins), open a PendingPayment order, and record the idempotency key -
///      ALL in one transaction, committed BEFORE any money moves. The order id is the stable
///      provider idempotency key.
///   3. Charge with NO database transaction open (a network call must never hold a DB lock).
///   4. Finalize in a second transaction:
///      - success  -> order + hold Confirmed + OrderConfirmed outboxed;
///      - decline  -> order PaymentFailed, hold back to Active (retry until TTL) or Expired;
///      - ambiguous (provider unreachable) -> stay PendingPayment (202) and let the reconciler settle it.
///
/// Because the hold is PaymentPending (not Active) the moment payment is in flight, the expiry
/// worker cannot reclaim a seat that is being paid for, and a crash leaves a durable order the
/// reconciler (or the client's retry) can complete without charging twice.
/// </summary>
public sealed class OrderService
{
    private readonly IOrderRepository _orders;
    private readonly IPaymentGateway _payments;
    private readonly IOutbox _outbox;
    private readonly TimeProvider _clock;
    private readonly HoldOptions _holdOptions;

    public OrderService(IOrderRepository orders, IPaymentGateway payments,
        IOutbox outbox, TimeProvider clock, HoldOptions holdOptions)
    {
        _orders = orders;
        _payments = payments;
        _outbox = outbox;
        _clock = clock;
        _holdOptions = holdOptions;
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

        // (1) Replay or recover an attempt already made under this idempotency key.
        if (idempotencyKey is not null)
        {
            var existing = await _orders.GetIdempotencyRecordAsync(tenantId, actorKey, idempotencyKey, ct);
            if (existing is not null)
            {
                if (!StringComparer.Ordinal.Equals(existing.RequestHash, requestHash))
                    return Result<OrderResponse>.Conflict("This idempotency key was already used for a different order request.");
                return await ResolveOrderAsync(existing.OrderId, ct);
            }
        }

        // (2) Validate and price the hold before taking the payment claim.
        var hold = await _orders.GetHoldForOrderAsync(request.HoldId, ct);
        if (hold is null)
            return Result<OrderResponse>.NotFound($"Hold '{request.HoldId}' was not found.");
        if (customerUserId is not null && hold.CustomerUserId != customerUserId)
            return Result<OrderResponse>.NotFound($"Hold '{request.HoldId}' was not found."); // ownership hidden as 404
        if (hold.Status != HoldStatus.Active || hold.IsExpired(now))
            return Result<OrderResponse>.Conflict($"Hold is '{(hold.IsExpired(now) ? "expired" : hold.Status.ToString())}' and cannot be purchased.");

        // (3) Claim + open order + record key, one transaction, committed BEFORE the charge.
        var orderId = Guid.NewGuid();
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
        IdempotencyRecord? idempotency = idempotencyKey is null ? null : new IdempotencyRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ActorKey = actorKey,
            Key = idempotencyKey,
            RequestHash = requestHash,
            OrderId = orderId,
            CreatedAt = now
        };

        hold.ClaimForPayment(now, now + _holdOptions.PaymentLease);
        _orders.Add(order);
        if (idempotency is not null)
            _orders.Add(idempotency);

        var claim = await _orders.TrySaveChangesAsync(ct);
        if (claim != SaveOutcome.Success)
        {
            // Lost the claim race or a same-key insert collided. Never a 500: replay the winner
            // when we can find it, otherwise the hold is simply no longer claimable.
            if (idempotencyKey is not null)
            {
                var winner = await _orders.GetIdempotencyRecordAsync(tenantId, actorKey, idempotencyKey, ct);
                if (winner is not null && winner.OrderId != orderId)
                    return await ResolveOrderAsync(winner.OrderId, ct);
            }
            return Result<OrderResponse>.Conflict("This hold is no longer available for checkout.");
        }

        // (4) Charge with the persisted order id as the stable key. No DB transaction is open.
        var payment = await _payments.ChargeAsync(
            new PaymentCharge(order.Id.ToString(), order.Amount, order.Currency), ct);

        // (5) Finalize in a second transaction.
        return await FinalizeAsync(order.Id, payment, ct);
    }

    /// <summary>Settle a freshly-charged order (or reconcile a recovered one) in one transaction.</summary>
    private async Task<Result<OrderResponse>> FinalizeAsync(Guid orderId, PaymentResult payment, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var order = await _orders.GetOrderWithHoldForUpdateAsync(orderId, ct);
        if (order is null)
            return Result<OrderResponse>.NotFound($"Order '{orderId}' was not found.");
        if (order.Status != OrderStatus.PendingPayment)
            return ResultForResolvedOrder(order); // a reconciler/retry already settled it

        var idempotency = await _orders.GetIdempotencyForOrderForUpdateAsync(orderId, ct);

        if (payment.Failure == PaymentFailure.ProviderUnavailable)
        {
            // Ambiguous: the charge may or may not have landed. Keep the durable claim and push
            // the lease out; reconciliation (or the client's retry) settles it. Nothing is lost.
            if (order.Hold.Status == HoldStatus.PaymentPending)
                order.Hold.ExtendPaymentLease(now + _holdOptions.PaymentLease);
            await _orders.SaveChangesAsync(ct);
            TicketingMetrics.OrdersPaymentUnavailable.Add(1);
            return Result<OrderResponse>.Success(Map(order)); // PendingPayment -> 202 Accepted
        }

        if (!payment.Succeeded)
        {
            SettleDeclined(order, idempotency, now);
            await _orders.SaveChangesAsync(ct);
            TicketingMetrics.OrdersPaymentDeclined.Add(1);
            return Result<OrderResponse>.Conflict("Payment was declined.");
        }

        SettleConfirmed(order, idempotency, payment.ProviderChargeId!, now);
        var save = await _orders.TrySaveChangesAsync(ct);
        if (save == SaveOutcome.ConcurrencyConflict)
        {
            var settled = await _orders.GetOrderWithHoldForUpdateAsync(orderId, ct);
            return settled is null
                ? Result<OrderResponse>.NotFound($"Order '{orderId}' was not found.")
                : ResultForResolvedOrder(settled);
        }
        TicketingMetrics.OrdersConfirmed.Add(1);
        return Result<OrderResponse>.Success(Map(order));
    }

    /// <summary>
    /// Replay or recover the order behind a known idempotency key. A still-PendingPayment order
    /// means a prior attempt never finalized (crash / lost response); ask the provider what
    /// really happened rather than charging again.
    /// </summary>
    private async Task<Result<OrderResponse>> ResolveOrderAsync(Guid orderId, CancellationToken ct)
    {
        var order = await _orders.GetOrderWithHoldForUpdateAsync(orderId, ct);
        if (order is null)
            return Result<OrderResponse>.Conflict("The idempotency record points to an order that no longer exists.");
        if (order.Status != OrderStatus.PendingPayment)
            return ResultForResolvedOrder(order);

        var inquiry = await _payments.GetChargeStatusAsync(orderId.ToString(), ct);
        return await ApplyInquiryAsync(order, inquiry, ct);
    }

    /// <summary>Apply a provider reconciliation verdict to a PendingPayment order. Shared by the
    /// retry path and the background reconciler.</summary>
    public async Task<Result<OrderResponse>> ApplyInquiryAsync(Order order, PaymentInquiry inquiry, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var idempotency = await _orders.GetIdempotencyForOrderForUpdateAsync(order.Id, ct);

        switch (inquiry.Outcome)
        {
            case PaymentOutcome.Charged:
                SettleConfirmed(order, idempotency, inquiry.ProviderChargeId!, now);
                break;
            case PaymentOutcome.NotCharged:
                SettleDeclined(order, idempotency, now);
                break;
            default: // Pending / Unknown: still ambiguous - keep the claim and try again later.
                if (order.Hold.Status == HoldStatus.PaymentPending)
                    order.Hold.ExtendPaymentLease(now + _holdOptions.PaymentLease);
                await _orders.SaveChangesAsync(ct);
                return Result<OrderResponse>.Success(Map(order));
        }

        var save = await _orders.TrySaveChangesAsync(ct);
        if (save == SaveOutcome.ConcurrencyConflict)
        {
            var settled = await _orders.GetOrderWithHoldForUpdateAsync(order.Id, ct);
            if (settled is not null)
                return ResultForResolvedOrder(settled);
        }
        return ResultForResolvedOrder(order);
    }

    private void SettleConfirmed(Order order, IdempotencyRecord? idempotency, string providerChargeId, DateTimeOffset now)
    {
        order.MarkConfirmed(providerChargeId);
        if (order.Hold.Status == HoldStatus.PaymentPending)
            order.Hold.ConfirmFromPayment(now);

        // Staged into the SAME transaction as the order + hold changes (the outbox pattern).
        _outbox.Add("OrderConfirmed", new
        {
            OrderId = order.Id,
            order.TenantId,
            order.Hold.TicketTypeId,
            order.Hold.Quantity,
            order.CustomerEmail,
            order.Amount,
            order.Currency
        });
        idempotency?.Complete(now);
    }

    private void SettleDeclined(Order order, IdempotencyRecord? idempotency, DateTimeOffset now)
    {
        order.MarkPaymentFailed();
        var hold = order.Hold;
        if (hold.Status == HoldStatus.PaymentPending)
        {
            if (hold.ExpiresAt > now)
            {
                hold.ReturnToActiveFromPayment(now); // TTL remains: buyer may retry on the same hold
            }
            else
            {
                hold.ExpireFromPayment(now);
                hold.TicketType.Inventory.Release(hold.Quantity); // no retry window left: give the seat back
                _outbox.Add("AvailabilityChanged", new { hold.TenantId, hold.TicketType.EventId, hold.TicketTypeId });
            }
        }
        idempotency?.Complete(now);
    }

    private static Result<OrderResponse> ResultForResolvedOrder(Order order) => order.Status switch
    {
        OrderStatus.Confirmed => Result<OrderResponse>.Success(Map(order)),
        OrderStatus.Refunded => Result<OrderResponse>.Success(Map(order)),
        OrderStatus.PaymentFailed => Result<OrderResponse>.Conflict("Payment was declined."),
        _ => Result<OrderResponse>.Success(Map(order)) // PendingPayment -> 202 Accepted
    };

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

        // Idempotent: an already-refunded order just replays; an in-flight one (claimed by a
        // concurrent caller or a prior attempt) resumes the SAME provider movement.
        if (order.Status == OrderStatus.Refunded)
            return Result<OrderResponse>.Success(Map(order));
        if (order.Status == OrderStatus.RefundPending)
            return await SettleRefundAsync(order, tenantId, actorKey, now, ct);
        if (order.Status != OrderStatus.Confirmed)
            return Result<OrderResponse>.Conflict($"Order is '{order.Status}' and cannot be refunded.");
        if (order.ProviderChargeId is null)
            return Result<OrderResponse>.Conflict("Order has no provider charge to refund.");

        // Policy: a scanned (admitted) ticket is non-refundable - the good was consumed.
        var ticket = await _orders.GetTicketForUpdateAsync(order.Id, ct);
        if (ticket is not null && ticket.Status == TicketStatus.Scanned)
            return Result<OrderResponse>.Conflict("Ticket has already been scanned; a used ticket cannot be refunded.");

        // Atomically claim Confirmed -> RefundPending BEFORE the provider call. The order's
        // concurrency token means only one caller wins the claim; the loser resolves to this
        // same order and re-attempts the provider with the SAME stable key (idempotent).
        order.MarkRefundPending();
        if (await _orders.TrySaveChangesAsync(ct) == SaveOutcome.ConcurrencyConflict)
        {
            var current = await _orders.GetForRefundAsync(orderId, ct);
            if (current is null)
                return Result<OrderResponse>.NotFound($"Order '{orderId}' was not found.");
            return current.Status switch
            {
                OrderStatus.Refunded => Result<OrderResponse>.Success(Map(current)),
                OrderStatus.RefundPending => await SettleRefundAsync(current, tenantId, actorKey, now, ct),
                _ => Result<OrderResponse>.Conflict($"Order is '{current.Status}' and cannot be refunded.")
            };
        }

        return await SettleRefundAsync(order, tenantId, actorKey, now, ct);
    }

    /// <summary>
    /// Call the provider with the STABLE per-order refund key and settle the RefundPending order.
    /// Shared by the claimant and any concurrent caller that resumes the in-flight refund, so the
    /// money moves once and inventory is credited once regardless of who or how many retry.
    /// </summary>
    private async Task<Result<OrderResponse>> SettleRefundAsync(
        Order order, Guid tenantId, string actorKey, DateTimeOffset now, CancellationToken ct)
    {
        var refund = await _payments.RefundAsync(
            new PaymentRefund($"refund:{order.Id:N}", order.ProviderChargeId!, order.Amount, order.Currency), ct);

        if (refund.Failure == PaymentFailure.ProviderUnavailable)
        {
            // Ambiguous: keep RefundPending. The stable key makes a later retry safe (one refund).
            TicketingMetrics.OrdersPaymentUnavailable.Add(1, new KeyValuePair<string, object?>("operation", "refund"));
            return Result<OrderResponse>.Unavailable("The payment provider is unavailable; please try again shortly.");
        }

        if (!refund.Succeeded)
        {
            order.RevertRefundClaim(); // release the claim so the order is not stuck
            await _orders.TrySaveChangesAsync(ct);
            return Result<OrderResponse>.Conflict("Refund was declined.");
        }

        order.MarkRefunded(refund.ProviderChargeId!, now);
        var ticket = await _orders.GetTicketForUpdateAsync(order.Id, ct);
        ticket?.Void(now);
        order.Hold.TicketType.Inventory.Release(order.Hold.Quantity); // credit the seat back, once

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
            RefundedByActor = actorKey, // the initiating actor, for audit
            order.CustomerEmail,
            order.Amount,
            order.Currency
        });

        if (await _orders.TrySaveChangesAsync(ct) == SaveOutcome.ConcurrencyConflict)
        {
            // A concurrent caller settled first; return that outcome without double-crediting.
            var settled = await _orders.GetForRefundAsync(order.Id, ct);
            return settled is null
                ? Result<OrderResponse>.NotFound($"Order '{order.Id}' was not found.")
                : Result<OrderResponse>.Success(Map(settled));
        }

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
