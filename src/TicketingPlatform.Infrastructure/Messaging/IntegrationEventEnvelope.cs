using System.Text.Json;
using RabbitMQ.Client.Events;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Infrastructure.Outbox;

namespace TicketingPlatform.Infrastructure.Messaging;

/// <summary>
/// Stable transport wrapper around a versioned business payload. Envelope evolution and payload
/// evolution are separate concerns: consumers route and dedupe from metadata, then deserialize
/// the payload version they support.
/// </summary>
internal sealed record IntegrationEventEnvelope(
    Guid MessageId,
    string EventType,
    int SchemaVersion,
    DateTimeOffset OccurredAt,
    Guid TenantId,
    string? CorrelationId,
    JsonElement Payload);

internal sealed class IntegrationEventContractException(string message) : Exception(message);

internal static class IntegrationEventEnvelopeCodec
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static byte[] Serialize(OutboxMessage message)
    {
        using var payloadDocument = JsonDocument.Parse(message.Payload);
        var payload = payloadDocument.RootElement.Clone();
        if (payload.ValueKind != JsonValueKind.Object)
            throw new IntegrationEventContractException("An integration-event payload must be a JSON object.");

        var tenantId = message.TenantId ?? ReadLegacyTenantId(payload);
        if (tenantId == Guid.Empty)
            throw new IntegrationEventContractException($"Outbox message '{message.Id}' has no tenant metadata.");
        if (message.SchemaVersion <= 0)
            throw new IntegrationEventContractException($"Outbox message '{message.Id}' has an invalid schema version.");

        return JsonSerializer.SerializeToUtf8Bytes(new IntegrationEventEnvelope(
            message.Id,
            message.Type,
            message.SchemaVersion,
            message.OccurredAt,
            tenantId,
            message.CorrelationId,
            payload), Json);
    }

    public static IntegrationEventEnvelope Read(BasicDeliverEventArgs delivery)
    {
        var envelope = JsonSerializer.Deserialize<IntegrationEventEnvelope>(delivery.Body.Span, Json)
            ?? throw new IntegrationEventContractException("The integration-event envelope is empty.");

        if (!Guid.TryParse(delivery.BasicProperties.MessageId, out var propertyMessageId)
            || propertyMessageId != envelope.MessageId)
            throw new IntegrationEventContractException("Envelope messageId does not match AMQP MessageId.");
        if (!string.Equals(envelope.EventType, delivery.RoutingKey, StringComparison.Ordinal))
            throw new IntegrationEventContractException("Envelope eventType does not match the routing key.");
        if (envelope.SchemaVersion != 1)
            throw new IntegrationEventContractException(
                $"Unsupported schema version {envelope.SchemaVersion} for '{envelope.EventType}'.");
        if (envelope.TenantId == Guid.Empty)
            throw new IntegrationEventContractException("Envelope tenantId is required.");
        if (envelope.Payload.ValueKind != JsonValueKind.Object)
            throw new IntegrationEventContractException("Envelope payload must be a JSON object.");

        return envelope;
    }

    public static T ReadPayload<T>(IntegrationEventEnvelope envelope) where T : IIntegrationEvent
    {
        var message = envelope.Payload.Deserialize<T>(Json)
            ?? throw new IntegrationEventContractException(
                $"Payload for '{envelope.EventType}' could not be deserialized as {typeof(T).Name}.");

        if (!string.Equals(message.EventType, envelope.EventType, StringComparison.Ordinal)
            || message.SchemaVersion != envelope.SchemaVersion)
            throw new IntegrationEventContractException(
                $"Payload contract {typeof(T).Name} does not match {envelope.EventType} v{envelope.SchemaVersion}.");

        return message;
    }

    private static Guid ReadLegacyTenantId(JsonElement payload)
    {
        if (payload.TryGetProperty("tenantId", out var tenant)
            && tenant.ValueKind == JsonValueKind.String
            && tenant.TryGetGuid(out var tenantId))
            return tenantId;

        return Guid.Empty;
    }
}
