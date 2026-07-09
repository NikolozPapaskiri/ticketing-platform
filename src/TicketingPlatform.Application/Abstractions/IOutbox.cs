namespace TicketingPlatform.Application.Abstractions;

/// <summary>
/// Port for the transactional outbox. Add() only STAGES the event; it is persisted by the SAME
/// SaveChanges as the caller's state change - one transaction, so a committed change always
/// has its event and a rolled-back change never does. The dispatcher publishes asynchronously.
/// </summary>
public interface IOutbox
{
    void Add(string type, object payload);
}
