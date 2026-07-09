namespace TicketingPlatform.Application.Common;

/// <summary>Bound from the "Holds" configuration section (registered as a singleton in Program).</summary>
public sealed class HoldOptions
{
    public const string SectionName = "Holds";

    /// <summary>How long a hold reserves inventory before the expiry service reclaims it.</summary>
    public int TtlSeconds { get; init; } = 600;

    /// <summary>How often the expiry background service scans for dead holds.</summary>
    public int ExpiryScanSeconds { get; init; } = 15;

    public TimeSpan Ttl => TimeSpan.FromSeconds(TtlSeconds);
    public TimeSpan ExpiryScanInterval => TimeSpan.FromSeconds(ExpiryScanSeconds);
}
