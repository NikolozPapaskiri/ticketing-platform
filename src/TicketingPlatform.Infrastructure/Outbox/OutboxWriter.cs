using System.Text.Json;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Infrastructure.Persistence;

namespace TicketingPlatform.Infrastructure.Outbox;

/// <summary>
/// Stages events into the outbox table THROUGH THE CALLER'S DbContext: because every scoped
/// repository shares this same context instance, the caller's SaveChanges persists the state
/// change and the event row in one database transaction. That single fact is the entire
/// outbox pattern.
/// </summary>
public sealed class OutboxWriter : IOutbox
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TicketingDbContext _db;
    private readonly TimeProvider _clock;

    public OutboxWriter(TicketingDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public void Add(string type, object payload)
    {
        _db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = type,
            Payload = JsonSerializer.Serialize(payload, Json),
            OccurredAt = _clock.GetUtcNow(),
            // Capture the current trace so the dispatcher can continue it (see OutboxMessage docs).
            TraceParent = System.Diagnostics.Activity.Current?.Id
        });
    }
}
