using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TicketingPlatform.Api.Common;

/// <summary>
/// JSON writer for the detailed, NON-gating health endpoint. Readiness (<c>/health/ready</c>) is a
/// terse traffic-routing decision; this view reports every dependency - including ones that must
/// not gate traffic, like the async broker - so an operator can see what is actually degraded
/// without conflating it with "take this pod out of the load balancer".
/// </summary>
public static class HealthResponse
{
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description,
                durationMs = entry.Value.Duration.TotalMilliseconds,
                tags = entry.Value.Tags,
                error = entry.Value.Exception?.Message
            })
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload));
    }
}
