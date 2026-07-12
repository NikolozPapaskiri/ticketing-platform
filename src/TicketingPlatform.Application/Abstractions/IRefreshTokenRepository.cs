using TicketingPlatform.Domain;

namespace TicketingPlatform.Application.Abstractions;

public interface IRefreshTokenRepository
{
    /// <summary>Tracked lookup by token hash, user included (refresh issues new tokens for that user).</summary>
    Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken ct);

    /// <summary>
    /// Atomically rotate <paramref name="parentHash"/> to <paramref name="child"/> in one
    /// transaction: the parent is revoked (and linked to the child) and the child inserted only if
    /// the parent was still active. Returns <c>true</c> for the single caller that won the claim;
    /// concurrent callers get <c>false</c> and must not have a token minted for them. This is the
    /// compare-and-swap that makes refresh rotation safe under parallel requests and replicas.
    /// </summary>
    Task<bool> TryRotateAsync(string parentHash, RefreshToken child, DateTimeOffset now, CancellationToken ct);

    /// <summary>Revokes every still-active token in a family (reuse detection and logout).</summary>
    Task<int> RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken ct);

    void Add(RefreshToken token);
    Task SaveChangesAsync(CancellationToken ct);
}
