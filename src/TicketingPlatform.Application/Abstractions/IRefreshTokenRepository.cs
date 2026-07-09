using TicketingPlatform.Domain;

namespace TicketingPlatform.Application.Abstractions;

public interface IRefreshTokenRepository
{
    /// <summary>Tracked lookup by token hash, user included (refresh issues new tokens for that user).</summary>
    Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken ct);

    /// <summary>All still-active tokens for a user - the reuse-detection path revokes the whole family.</summary>
    Task<IReadOnlyList<RefreshToken>> GetActiveForUserAsync(Guid userId, CancellationToken ct);

    void Add(RefreshToken token);
    Task SaveChangesAsync(CancellationToken ct);
}
