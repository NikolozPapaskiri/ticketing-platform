using System.Text.Json.Serialization;
using TicketingPlatform.Application.Abstractions;

namespace TicketingPlatform.Application.Contracts;

/// <summary>Stable broker routing names. Renaming a CLR record must not rename a public event.</summary>
public static class IntegrationEventNames
{
    public const string AvailabilityChanged = "AvailabilityChanged";
    public const string OrderConfirmed = "OrderConfirmed";
    public const string OrderRefunded = "OrderRefunded";
}

public sealed record AvailabilityChangedIntegrationEvent(
    [property: JsonIgnore] Guid TenantId,
    Guid EventId,
    Guid TicketTypeId) : IIntegrationEvent
{
    [JsonIgnore] public string EventType => IntegrationEventNames.AvailabilityChanged;
    [JsonIgnore] public int SchemaVersion => 1;
}

public sealed record OrderConfirmedIntegrationEvent(
    [property: JsonIgnore] Guid TenantId,
    Guid OrderId,
    Guid TicketTypeId,
    int Quantity,
    string CustomerEmail,
    decimal Amount,
    string Currency) : IIntegrationEvent
{
    [JsonIgnore] public string EventType => IntegrationEventNames.OrderConfirmed;
    [JsonIgnore] public int SchemaVersion => 1;
}

public sealed record OrderRefundedIntegrationEvent(
    [property: JsonIgnore] Guid TenantId,
    Guid OrderId,
    string RefundedByActor,
    string CustomerEmail,
    decimal Amount,
    string Currency) : IIntegrationEvent
{
    [JsonIgnore] public string EventType => IntegrationEventNames.OrderRefunded;
    [JsonIgnore] public int SchemaVersion => 1;
}
