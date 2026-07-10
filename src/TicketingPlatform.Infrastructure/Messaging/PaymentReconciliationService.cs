using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Application.Common;
using TicketingPlatform.Application.Services;
using TicketingPlatform.Domain;

namespace TicketingPlatform.Infrastructure.Messaging;

/// <summary>
/// The payment saga's self-healing arm: finds orders stuck in PendingPayment whose hold lease has
/// expired (a checkout that charged-then-crashed, or whose provider response was lost) and asks
/// the provider what actually happened - confirming, failing, or extending each one. This is why
/// a lost response never strands money or a seat: the client's retry OR this worker will settle it.
///
/// Multi-replica safe WITHOUT leader election: two replicas may pick the same order, but the
/// order/hold concurrency tokens mean only one finalize wins; the other sees the conflict and
/// backs off. GetChargeStatus only READS from the provider, so a double check never double-charges.
/// </summary>
public sealed class PaymentReconciliationService : BackgroundService
{
    private const int BatchSize = 50;

    private readonly IServiceScopeFactory _scopes;
    private readonly HoldOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<PaymentReconciliationService> _logger;

    public PaymentReconciliationService(IServiceScopeFactory scopes, HoldOptions options,
        TimeProvider clock, ILogger<PaymentReconciliationService> logger)
    {
        _scopes = scopes;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileBatchAsync(stoppingToken);
                await Task.Delay(_options.ReconcileScanInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Payment reconciliation tick failed; retrying next interval");
                try { await Task.Delay(_options.ReconcileScanInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ReconcileBatchAsync(CancellationToken ct)
    {
        IReadOnlyList<Guid> ids;
        using (var scope = _scopes.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IOrderRepository>();
            ids = await repo.GetOrderIdsWithExpiredPaymentLeaseAsync(_clock.GetUtcNow(), BatchSize, ct);
        }

        if (ids.Count == 0)
            return;

        var reconciled = 0;
        foreach (var id in ids)
        {
            // A fresh scope per order keeps each reconciliation's transaction independent.
            using var scope = _scopes.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IOrderRepository>();
            var orders = scope.ServiceProvider.GetRequiredService<OrderService>();
            var payments = scope.ServiceProvider.GetRequiredService<IPaymentGateway>();

            var order = await repo.GetOrderWithHoldForUpdateAsync(id, ct);
            if (order is null || order.Status != OrderStatus.PendingPayment)
                continue; // already settled by the client's retry or another replica

            var inquiry = await payments.GetChargeStatusAsync(id.ToString(), ct);
            await orders.ApplyInquiryAsync(order, inquiry, ct);
            reconciled++;
        }

        if (reconciled > 0)
            _logger.LogInformation("Reconciled {Count} pending payment(s)", reconciled);
    }
}
