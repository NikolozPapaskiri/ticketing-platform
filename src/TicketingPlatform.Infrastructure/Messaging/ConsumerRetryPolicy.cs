using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace TicketingPlatform.Infrastructure.Messaging;

/// <summary>
/// Shared failure policy for every RabbitMQ consumer. Malformed messages are poison and park
/// immediately. Operational failures are copied to a durable TTL queue and acknowledged only
/// after RabbitMQ confirms that copy. The TTL dead-letters them back to the original event route.
/// </summary>
internal static class ConsumerRetryPolicy
{
    internal const string AttemptHeader = "x-ticketing-consumer-attempt";

    public static async Task HandleFailureAsync(IChannel channel, BasicDeliverEventArgs delivery,
        string consumerQueue, Exception failure, RabbitMqOptions options, ILogger logger, CancellationToken ct)
    {
        var attempt = ReadAttempt(delivery.BasicProperties.Headers);
        var maxAttempts = Math.Max(1, options.ConsumerMaxAttempts);

        if (IsPoison(failure))
        {
            logger.LogError(failure,
                "Poison message {MessageId} in {ConsumerQueue}; dead-lettering without retry",
                delivery.BasicProperties.MessageId, consumerQueue);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, ct);
            return;
        }

        if (attempt >= maxAttempts)
        {
            logger.LogError(failure,
                "Message {MessageId} in {ConsumerQueue} exhausted {Attempts} attempts; dead-lettering",
                delivery.BasicProperties.MessageId, consumerQueue, attempt);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, ct);
            return;
        }

        var nextAttempt = attempt + 1;
        var retryQueue = RabbitMqTopology.RetryQueueName(consumerQueue, delivery.RoutingKey);
        var headers = delivery.BasicProperties.Headers is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(delivery.BasicProperties.Headers);
        headers[AttemptHeader] = nextAttempt;

        var properties = new BasicProperties
        {
            MessageId = delivery.BasicProperties.MessageId,
            CorrelationId = delivery.BasicProperties.CorrelationId,
            ContentType = delivery.BasicProperties.ContentType,
            ContentEncoding = delivery.BasicProperties.ContentEncoding,
            Type = delivery.BasicProperties.Type,
            AppId = delivery.BasicProperties.AppId,
            Persistent = true,
            Headers = headers
        };

        try
        {
            // The consumer channel has confirm tracking enabled. ACK the original only after the
            // retry copy is durably accepted; otherwise a broker interruption could lose it.
            await channel.BasicPublishAsync(string.Empty, retryQueue, mandatory: true, properties,
                delivery.Body, ct);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
            logger.LogWarning(failure,
                "Message {MessageId} in {ConsumerQueue} scheduled for retry {Attempt}/{MaxAttempts}",
                delivery.BasicProperties.MessageId, consumerQueue, nextAttempt, maxAttempts);
        }
        catch (Exception publishFailure) when (!ct.IsCancellationRequested)
        {
            logger.LogError(publishFailure,
                "Could not schedule retry for message {MessageId} in {ConsumerQueue}; requeueing original",
                delivery.BasicProperties.MessageId, consumerQueue);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, ct);
        }
    }

    private static int ReadAttempt(IDictionary<string, object?>? headers)
    {
        if (headers?.TryGetValue(AttemptHeader, out var raw) != true)
            return 1;

        return raw switch
        {
            int value => value,
            long value when value <= int.MaxValue => (int)value,
            byte value => value,
            byte[] bytes when int.TryParse(Encoding.UTF8.GetString(bytes), out var value) => value,
            _ => 1
        };
    }

    private static bool IsPoison(Exception failure) => failure is
        JsonException or IntegrationEventContractException or FormatException
        or KeyNotFoundException or ArgumentException;
}
