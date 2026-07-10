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

    public TimeSpan AdmitInterval => TimeSpan.FromSeconds(AdmitIntervalSeconds);
    public TimeSpan AdmissionTtl => TimeSpan.FromSeconds(AdmissionTtlSeconds);
}
