using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using TicketingPlatform.Infrastructure.Messaging;

namespace TicketingPlatform.Infrastructure.Health;

/// <summary>
/// Readiness check for RabbitMQ: holds one cached connection open and reports on its state.
/// If the broker is down, readiness flips and Kubernetes stops routing traffic here until the
/// outbox/consumer chain can function again.
/// </summary>
public sealed class RabbitMqHealthCheck : IHealthCheck, IDisposable
{
    private readonly RabbitMqOptions _options;
    private IConnection? _connection;

    public RabbitMqHealthCheck(IOptions<RabbitMqOptions> options) => _options = options.Value;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            if (_connection is not { IsOpen: true })
            {
                _connection?.Dispose();
                var factory = new ConnectionFactory
                {
                    HostName = _options.HostName,
                    Port = _options.Port,
                    UserName = _options.UserName,
                    Password = _options.Password
                };
                _connection = await factory.CreateConnectionAsync(ct);
            }

            return HealthCheckResult.Healthy("connection open");
        }
        catch (Exception ex)
        {
            _connection = null;
            return HealthCheckResult.Unhealthy("RabbitMQ unreachable", ex);
        }
    }

    public void Dispose() => _connection?.Dispose();
}
