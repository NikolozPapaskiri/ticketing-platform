namespace TicketingPlatform.Domain;

/// <summary>
/// A server-side record of an issued refresh token. Only the SHA-256 hash is stored - a DB
/// leak must not hand out usable tokens (same reasoning as password hashing).
/// Rotation: every refresh revokes this token and issues a new one (ReplacedByTokenHash links
/// the chain). Reuse detection: presenting an already-revoked token means the token leaked,
/// so the WHOLE family for that user is revoked and the user must log in again.
/// This is also the honest answer to "how do you revoke a JWT": you cannot revoke a stateless
/// access token - you keep it short-lived and revoke the long-lived refresh token server-side.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>
    /// Groups a rotation chain (one login = one family). Reuse detection and logout revoke by
    /// FAMILY, so a compromise or sign-out on one device leaves other devices' sessions alone -
    /// unlike revoking every token the user owns.
    /// </summary>
    public Guid FamilyId { get; set; }

    public required string TokenHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; private set; }
    public string? ReplacedByTokenHash { get; private set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;

    public void Revoke(DateTimeOffset now, string? replacedByTokenHash = null)
    {
        RevokedAt ??= now; // idempotent: first revocation wins
        ReplacedByTokenHash ??= replacedByTokenHash;
    }
}
