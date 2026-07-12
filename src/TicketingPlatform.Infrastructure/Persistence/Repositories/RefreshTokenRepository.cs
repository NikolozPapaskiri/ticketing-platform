using Microsoft.EntityFrameworkCore;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Domain;

namespace TicketingPlatform.Infrastructure.Persistence.Repositories;

public sealed class RefreshTokenRepository : IRefreshTokenRepository
{
    private readonly TicketingDbContext _db;
    public RefreshTokenRepository(TicketingDbContext db) => _db = db;

    public Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken ct) =>
        // No-tracking + user included. Rotation and revocation are both set-based ExecuteUpdate
        // calls, so nothing here mutates a tracked entity - and crucially, the refresh flow may
        // read the same hash twice (once before an atomic claim, once after losing it). Tracking
        // would make the second read return the stale first instance from the identity map;
        // AsNoTracking guarantees the post-claim read reflects the concurrent rotation.
        _db.RefreshTokens
            .AsNoTracking()
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);

    public async Task<bool> TryRotateAsync(string parentHash, RefreshToken child, DateTimeOffset now, CancellationToken ct)
    {
        // The conditional UPDATE ... WHERE RevokedAt IS NULL is the compare-and-swap: under
        // READ COMMITTED a second concurrent rotation blocks on the row lock, then re-evaluates
        // the predicate against the just-revoked row and updates 0 rows. Wrapping the claim and
        // the child insert in one transaction means we never leave a revoked parent without its
        // successor (and never mint a child for the caller that lost the claim).
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var claimed = await _db.RefreshTokens
            .Where(t => t.TokenHash == parentHash && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.RevokedAt, now)
                .SetProperty(t => t.ReplacedByTokenHash, child.TokenHash), ct);

        if (claimed == 0)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        _db.RefreshTokens.Add(child);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public Task<int> RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken ct) =>
        // Set-based revoke of every live token in the family - the reuse-detection and logout
        // "kill the whole session" operation. Idempotent: already-revoked rows are excluded.
        _db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

    public void Add(RefreshToken token) => _db.RefreshTokens.Add(token);

    public Task SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}
