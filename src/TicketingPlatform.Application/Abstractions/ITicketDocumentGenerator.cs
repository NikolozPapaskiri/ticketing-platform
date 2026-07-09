namespace TicketingPlatform.Application.Abstractions;

/// <summary>Port for rendering a ticket document; the PDF library is an Infrastructure detail.</summary>
public interface ITicketDocumentGenerator
{
    byte[] Generate(TicketDocumentData data);
}

public sealed record TicketDocumentData(
    Guid OrderId,
    string EventName,
    string? VenueName,
    DateTimeOffset StartsAt,
    string TicketTypeName,
    int Quantity,
    string CustomerEmail,
    decimal Amount,
    string Currency);
