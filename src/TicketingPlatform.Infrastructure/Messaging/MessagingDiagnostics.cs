using System.Diagnostics;

namespace TicketingPlatform.Infrastructure.Messaging;

/// <summary>
/// The ActivitySource for the messaging hops. HTTP and HttpClient spans come free from the
/// OpenTelemetry instrumentation packages; the outbox POLLING breaks the trace chain (the
/// dispatcher runs in no request's context), so continuity is rebuilt by hand: the outbox row
/// stores the W3C traceparent captured at write time, the dispatcher starts a Producer span
/// under that parent and stamps it into the message headers, and the consumer starts a
/// Consumer span under the header's parent. One trace: request -> outbox -> broker -> consumer.
/// </summary>
public static class MessagingDiagnostics
{
    public const string SourceName = "TicketingPlatform.Messaging";
    public static readonly ActivitySource Source = new(SourceName);
}
