using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace TicketingPlatform.Infrastructure.Messaging;

/// <summary>
/// Broker transport boundary for the outbox dispatcher. It owns the long-lived confirm-enabled
/// channel and recreates it after connection loss; the dispatcher owns database claims/retries.
/// </summary>
public interface IOutboxPublisher
{
    Task PublishAsync(string routingKey, BasicProperties properties, ReadOnlyMemory<byte> body,
        CancellationToken ct);
}

public sealed class RabbitMqOutboxPublisher : IOutboxPublisher, IAsyncDisposable
{
    private readonly RabbitMqOptions _options;
    private IConnection? _connection;
    private IChannel? _channel;

    public RabbitMqOutboxPublisher(IOptions<RabbitMqOptions> options) => _options = options.Value;

    public async Task PublishAsync(string routingKey, BasicProperties properties, ReadOnlyMemory<byte> body,
        CancellationToken ct)
    {
        var channel = await EnsureChannelAsync(ct);
        await channel.BasicPublishAsync(_options.Exchange, routingKey, mandatory: true, properties, body, ct);
    }

    private async Task<IChannel> EnsureChannelAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true })
            return _channel;

        if (_channel is not null) await _channel.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
        _channel = null;
        _connection = null;

        var factory = new ConnectionFactory
        {
            HostName = _options.HostName,
            Port = _options.Port,
            UserName = _options.UserName,
            Password = _options.Password
        };

        _connection = await factory.CreateConnectionAsync(ct);
        _channel = await _connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
        await RabbitMqTopology.DeclareAsync(_channel, _options, ct);
        return _channel;
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null) await _channel.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
    }
}
