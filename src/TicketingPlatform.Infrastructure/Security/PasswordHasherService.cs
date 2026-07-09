using Microsoft.AspNetCore.Identity;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Domain;

namespace TicketingPlatform.Infrastructure.Security;

/// <summary>
/// Wraps ASP.NET Core Identity's PasswordHasher: PBKDF2-HMAC-SHA512, 100k iterations, salted,
/// version-tagged so the algorithm can be upgraded later. Never roll your own password hashing.
/// </summary>
public sealed class PasswordHasherService : IPasswordHasherService
{
    private readonly PasswordHasher<User> _hasher = new();

    public string Hash(User user, string password) =>
        _hasher.HashPassword(user, password);

    public bool Verify(User user, string hashedPassword, string providedPassword) =>
        // SuccessRehashNeeded still means the password was correct - it just signals the hash
        // was created with older settings and could be re-hashed on this login.
        _hasher.VerifyHashedPassword(user, hashedPassword, providedPassword)
            is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
}
