using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace TicketingPlatform.Infrastructure.Messaging;

/// <summary>
/// Declares the whole broker topology once, at startup, BEFORE the dispatcher can publish.
/// Registered ahead of the dispatcher and consumers; because it is a plain IHostedService (not a
/// BackgroundService) its StartAsync is awaited to completion by the host before the next hosted
/// service starts, so the exchange, queues, and bindings provably exist before the first publish.
/// If the broker cannot be reached at startup it throws - the dispatcher will still re-declare
/// defensively, but topology is never left to a race between consumer startups.
/// </summary>
public sealed class RabbitMqTopologyInitializer : IHostedService
{
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqTopologyInitializer> _logger;

    public RabbitMqTopologyInitializer(IOptions<RabbitMqOptions> options, ILogger<RabbitMqTopologyInitializer> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = _options.HostName,
            Port = _options.Port,
            UserName = _options.UserName,
            Password = _options.Password
        };

        await using var connection = await factory.CreateConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        await RabbitMqTopology.DeclareAsync(channel, _options, cancellationToken);

        _logger.LogInformation("RabbitMQ topology declared (exchange, DLX, and all consumer queues + bindings)");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
