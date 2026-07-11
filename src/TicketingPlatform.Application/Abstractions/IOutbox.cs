namespace TicketingPlatform.Application.Abstractions;

/// <summary>
/// Port for the transactional outbox. Add() only STAGES the event; it is persisted by the SAME
/// SaveChanges as the caller's state change - one transaction, so a committed change always
/// has its event and a rolled-back change never does. The dispatcher publishes asynchronously.
/// </summary>
public interface IOutbox
{
    void Add(IIntegrationEvent message);
}

/// <summary>
/// Compile-time contract for messages that leave the application boundary. The outbox persists
/// these metadata values separately from the payload so a dispatcher never has to infer them.
/// </summary>
public interface IIntegrationEvent
{
    string EventType { get; }
    int SchemaVersion { get; }
    Guid TenantId { get; }
}
