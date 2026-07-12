namespace TicketingPlatform.Application.Common;

/// <summary>
/// Refresh-session tuning. Kept in Application (a plain injected singleton) so the auth service
/// stays free of <c>IOptions</c> and framework types.
/// </summary>
public sealed class AuthSessionOptions
{
    public const string SectionName = "Auth";

    /// <summary>
    /// How long after a token is rotated a replay of the OLD token is still treated as a
    /// legitimate concurrent/near-concurrent refresh (a fresh sibling in the same family is
    /// issued) rather than token theft. Covers the real BFF case where two parallel requests -
    /// possibly on different web replicas - both present the same cookie before either response
    /// updates it. A replay AFTER this window revokes the whole family. Set to 0 to disable the
    /// window entirely (any replay of a rotated token is theft). Seconds.
    /// </summary>
    public int RefreshRotationGraceSeconds { get; init; } = 5;
}
