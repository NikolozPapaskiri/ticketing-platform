using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace TicketingPlatform.Infrastructure.Health;

/// <summary>
/// Readiness check for Redis: one PING over a lazily created, cached multiplexer (a probe
/// every few seconds must not open a fresh connection every time). Registered as a singleton.
/// </summary>
public sealed class RedisHealthCheck : IHealthCheck, IDisposable
{
    private readonly string _connectionString;
    private ConnectionMultiplexer? _connection;

    public RedisHealthCheck(IConfiguration configuration) =>
        _connectionString = configuration.GetConnectionString("Redis")
            ?? throw new InvalidOperationException("Missing 'ConnectionStrings:Redis' configuration.");

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            _connection ??= await ConnectionMultiplexer.ConnectAsync(_connectionString);
            var latency = await _connection.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy($"PING {latency.TotalMilliseconds:F0}ms");
        }
        catch (Exception ex)
        {
            _connection?.Dispose();
            _connection = null; // reconnect on the next probe
            return HealthCheckResult.Unhealthy("Redis unreachable", ex);
        }
    }

    public void Dispose() => _connection?.Dispose();
}
