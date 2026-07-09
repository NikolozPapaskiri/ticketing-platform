using System.Security.Cryptography;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Application.Common;
using TicketingPlatform.Application.Contracts;
using TicketingPlatform.Domain;

namespace TicketingPlatform.Application.Services;

/// <summary>
/// Authentication use cases: register, login, refresh-token rotation with reuse detection.
/// Access tokens are short-lived stateless JWTs; refresh tokens are long-lived, stored hashed
/// server-side, and revocable - which is the honest answer to "how do you revoke a JWT".
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

    public AuthService(
        IUserRepository users,
        IRefreshTokenRepository refreshTokens,
        ITenantRepository tenants,
        IPasswordHasherService hasher,
        IJwtTokenGenerator jwt,
        TimeProvider clock)
    {
        _users = users;
        _refreshTokens = refreshTokens;
        _tenants = tenants;
        _hasher = hasher;
        _jwt = jwt;
        _clock = clock;
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

        return Result<AuthResponse>.Success(await IssueTokenPairAsync(user, ct));
    }

    public async Task<Result<AuthResponse>> RefreshAsync(RefreshRequest request, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var stored = await _refreshTokens.GetByHashAsync(HashToken(request.RefreshToken), ct);

        if (stored is null)
            return Result<AuthResponse>.Unauthorized("Invalid refresh token.");

        // REUSE DETECTION: a token that exists but is no longer active was already rotated or
        // revoked. Someone is replaying it (the legitimate client holds the newer one), so the
        // entire family is revoked - both the thief's copy and the stolen session die together.
        if (!stored.IsActive(now))
        {
            foreach (var token in await _refreshTokens.GetActiveForUserAsync(stored.UserId, ct))
                token.Revoke(now);
            await _refreshTokens.SaveChangesAsync(ct);
            return Result<AuthResponse>.Unauthorized("Invalid refresh token.");
        }

        // ROTATION: every refresh replaces the token; the old one can never be used again.
        var response = await IssueTokenPairAsync(stored.User, ct, beforeSave: newHash => stored.Revoke(now, newHash));
        return Result<AuthResponse>.Success(response);
    }

    private async Task<AuthResponse> IssueTokenPairAsync(User user, CancellationToken ct, Action<string>? beforeSave = null)
    {
        var (accessToken, expiresAt) = _jwt.Generate(user);

        // 64 random bytes, opaque to the client; only the SHA-256 goes to the database.
        var rawRefreshToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var refreshHash = HashToken(rawRefreshToken);
        var now = _clock.GetUtcNow();

        beforeSave?.Invoke(refreshHash);
        _refreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = refreshHash,
            CreatedAt = now,
            ExpiresAt = now.Add(RefreshTokenLifetime)
        });
        await _refreshTokens.SaveChangesAsync(ct);

        return new AuthResponse(accessToken, expiresAt, rawRefreshToken);
    }

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
