namespace TicketingPlatform.Domain;

public class InvalidStatusTransitionException : Exception
{
    public EventStatus From { get; }
    public EventStatus To { get; }

    public InvalidStatusTransitionException(EventStatus from, EventStatus to)
        : base($"Cannot transition from {from} to {to}")
    {
        From = from;
        To = to;
    }
}
