namespace TicketingPlatform.Application.Common;

/// <summary>Bound from the "WaitingRoom" configuration section (plain singleton, like HoldOptions).</summary>
public sealed class WaitingRoomOptions
{
    public const string SectionName = "WaitingRoom";

    /// <summary>How many visitors each admission tick lets through - THE load-leveling valve.</summary>
    public int AdmitBatchSize { get; init; } = 5;

    /// <summary>Seconds between admission ticks.</summary>
    public int AdmitIntervalSeconds { get; init; } = 5;

    /// <summary>How long an admission stays valid (time to pick tickets and check out).</summary>
    public int AdmissionTtlSeconds { get; init; } = 300;

    /// <summary>
    /// The GLOBAL long-term admissions-per-second, enforced by a Redis token bucket inside the
    /// admission script. Because the bucket lives in Redis (not per-process), the effective rate
    /// stays constant no matter how many API/worker replicas run an admitter - the replica count
    /// is a throughput detail, not a rate multiplier. Separate from AdmitBatchSize (a per-call
    /// efficiency cap that must not change the long-term rate).
    /// </summary>
    public double AdmitRatePerSecond { get; init; } = 1.0;

    /// <summary>Token-bucket capacity: the largest burst of admissions allowed after an idle gap.</summary>
    public int AdmitBurst { get; init; } = 5;

    /// <summary>
    /// How many holds ONE admission may authorize before it is used up. A grant is not an
    /// open door: it is consumed as it is spent, so a leaked visitor id can't reserve forever.
    /// </summary>
    public int AdmissionHoldQuota { get; init; } = 4;

    /// <summary>Maximum queue joins one client (by IP) may make within the window below.</summary>
    public int JoinRateLimit { get; init; } = 20;

    /// <summary>Window for the per-client join rate limit (bounds queue-position minting).</summary>
    public int JoinRateWindowSeconds { get; init; } = 60;

    public TimeSpan AdmitInterval => TimeSpan.FromSeconds(AdmitIntervalSeconds);
    public TimeSpan AdmissionTtl => TimeSpan.FromSeconds(AdmissionTtlSeconds);
}
