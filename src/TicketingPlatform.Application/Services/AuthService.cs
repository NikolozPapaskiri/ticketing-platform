using System.Security.Cryptography;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Application.Common;
using TicketingPlatform.Application.Contracts;
using TicketingPlatform.Domain;

namespace TicketingPlatform.Application.Services;

/// <summary>
/// Authentication use cases: register, login, refresh-token rotation with reuse detection, and
/// server-side logout. Access tokens are short-lived stateless JWTs; refresh tokens are
/// long-lived, stored hashed server-side, family-scoped, and revocable - which is the honest
/// answer to "how do you revoke a JWT".
/// </summary>
public sealed class AuthService
{
    private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(7);

    private readonly IUserRepository _users;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly ITenantRepository _tenants;
    private readonly IPasswordHasherService _hasher;
    private readonly IJwtTokenGenerator _jwt;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _rotationGrace;

    public AuthService(
        IUserRepository users,
        IRefreshTokenRepository refreshTokens,
        ITenantRepository tenants,
        IPasswordHasherService hasher,
        IJwtTokenGenerator jwt,
        TimeProvider clock,
        AuthSessionOptions options)
    {
        _users = users;
        _refreshTokens = refreshTokens;
        _tenants = tenants;
        _hasher = hasher;
        _jwt = jwt;
        _clock = clock;
        _rotationGrace = TimeSpan.FromSeconds(Math.Max(0, options.RefreshRotationGraceSeconds));
    }

    /// <summary>Self-service registration is always a Customer - roles are never client-chosen.</summary>
    public async Task<Result<UserResponse>> RegisterCustomerAsync(RegisterRequest request, CancellationToken ct)
    {
        var normalized = Normalize(request.Email);
        if (await _users.EmailExistsAsync(normalized, ct))
            return Result<UserResponse>.Conflict("An account with this email already exists.");

        var user = NewUser(request.Email, normalized, UserRole.Customer, tenantId: null);
        user.PasswordHash = _hasher.Hash(user, request.Password);

        _users.Add(user);
        await _users.SaveChangesAsync(ct);

        return Result<UserResponse>.Success(Map(user));
    }

    /// <summary>Staff/admin accounts are provisioned by a PlatformAdmin (enforced at the endpoint).</summary>
    public async Task<Result<UserResponse>> RegisterStaffAsync(RegisterStaffRequest request, CancellationToken ct)
    {
        var normalized = Normalize(request.Email);
        if (await _users.EmailExistsAsync(normalized, ct))
            return Result<UserResponse>.Conflict("An account with this email already exists.");

        var role = Enum.Parse<UserRole>(request.Role); // validator guarantees a legal value

        // Staff must point at a real tenant - a claim for a nonexistent tenant would still
        // pass the query filters (they only compare ids), so reject it at provisioning time.
        if (role == UserRole.OrganizerStaff && !await _tenants.ExistsAsync(request.TenantId!.Value, ct))
            return Result<UserResponse>.NotFound($"Tenant '{request.TenantId}' was not found.");

        var user = NewUser(request.Email, normalized, role, role == UserRole.OrganizerStaff ? request.TenantId : null);
        user.PasswordHash = _hasher.Hash(user, request.Password);

        _users.Add(user);
        await _users.SaveChangesAsync(ct);

        return Result<UserResponse>.Success(Map(user));
    }

    public async Task<Result<AuthResponse>> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var user = await _users.GetByEmailAsync(Normalize(request.Email), ct);

        // Same response for unknown email and wrong password - no account enumeration.
        if (user is null || !_hasher.Verify(user, user.PasswordHash, request.Password))
            return Result<AuthResponse>.Unauthorized("Invalid credentials.");

        // A login starts a brand-new session family (this device/browser).
        return Result<AuthResponse>.Success(await IssueTokenAsync(user, Guid.NewGuid(), ct));
    }

    public async Task<Result<AuthResponse>> RefreshAsync(RefreshRequest request, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var parentHash = HashToken(request.RefreshToken);
        var stored = await _refreshTokens.GetByHashAsync(parentHash, ct);

        if (stored is null)
            return Result<AuthResponse>.Unauthorized("Invalid refresh token.");

        // ROTATION: the single caller that wins the atomic claim rotates the token and gets the
        // new pair. Everyone else either lost a concurrent race (handled by the grace window
        // below) or is replaying an already-rotated token.
        if (stored.IsActive(now))
        {
            var raw = NewRawToken();
            var child = NewToken(stored.UserId, stored.FamilyId, HashToken(raw), now);
            if (await _refreshTokens.TryRotateAsync(parentHash, child, now, ct))
            {
                var (accessToken, expiresAt) = _jwt.Generate(stored.User);
                return Result<AuthResponse>.Success(new AuthResponse(accessToken, expiresAt, raw));
            }

            // Lost the claim to a concurrent refresh: re-read the now-rotated parent so the grace
            // logic below can tell a legitimate parallel request apart from a real replay.
            stored = await _refreshTokens.GetByHashAsync(parentHash, ct);
            if (stored is null)
                return Result<AuthResponse>.Unauthorized("Invalid refresh token.");
        }

        // Past here the presented token is not active: rotated (grace or theft), revoked by
        // logout, or simply expired. Only a ROTATED token (linked to a successor) can be a
        // legitimate concurrent refresh; a logout-revoked token has no successor link.
        var wasRotated = stored.RevokedAt is not null && stored.ReplacedByTokenHash is not null;

        // GRACE: a rotated token replayed within the window is a legitimate concurrent or
        // near-concurrent refresh (two parallel BFF requests, possibly on different replicas,
        // carrying the same cookie). Issue a sibling in the SAME family; do NOT cry theft.
        if (wasRotated && now - stored.RevokedAt!.Value < _rotationGrace)
            return Result<AuthResponse>.Success(await IssueTokenAsync(stored.User, stored.FamilyId, ct));

        // REUSE DETECTION: a rotated token replayed OUTSIDE the grace window means the real token
        // leaked (the legitimate client holds the newer one). Revoke the whole family so the
        // thief's copy and the stolen session die together - but only THIS family, not the user's
        // other devices.
        if (wasRotated)
            await _refreshTokens.RevokeFamilyAsync(stored.FamilyId, now, ct);

        return Result<AuthResponse>.Unauthorized("Invalid refresh token.");
    }

    /// <summary>Server-side sign-out: kills the whole family so no rotated sibling survives. Idempotent.</summary>
    public async Task LogoutAsync(RefreshRequest request, CancellationToken ct)
    {
        var stored = await _refreshTokens.GetByHashAsync(HashToken(request.RefreshToken), ct);
        if (stored is null)
            return; // unknown/garbage token: nothing to revoke, and we reveal nothing

        await _refreshTokens.RevokeFamilyAsync(stored.FamilyId, _clock.GetUtcNow(), ct);
    }

    private async Task<AuthResponse> IssueTokenAsync(User user, Guid familyId, CancellationToken ct)
    {
        var (accessToken, expiresAt) = _jwt.Generate(user);

        var raw = NewRawToken();
        _refreshTokens.Add(NewToken(user.Id, familyId, HashToken(raw), _clock.GetUtcNow()));
        await _refreshTokens.SaveChangesAsync(ct);

        return new AuthResponse(accessToken, expiresAt, raw);
    }

    private static RefreshToken NewToken(Guid userId, Guid familyId, string tokenHash, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        FamilyId = familyId,
        TokenHash = tokenHash,
        CreatedAt = now,
        ExpiresAt = now.Add(RefreshTokenLifetime)
    };

    // 64 random bytes, opaque to the client; only the SHA-256 hash is ever stored.
    private static string NewRawToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

    private static User NewUser(string email, string normalized, UserRole role, Guid? tenantId) => new()
    {
        Id = Guid.NewGuid(),
        Email = email,
        NormalizedEmail = normalized,
        PasswordHash = string.Empty, // set right after construction; required member needs a value
        Role = role,
        TenantId = tenantId,
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static string Normalize(string email) => email.Trim().ToUpperInvariant();

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    private static UserResponse Map(User user) =>
        new(user.Id, user.Email, user.Role.ToString(), user.TenantId);
}
