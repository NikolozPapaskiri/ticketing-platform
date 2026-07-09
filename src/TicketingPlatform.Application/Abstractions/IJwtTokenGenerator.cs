using TicketingPlatform.Domain;

namespace TicketingPlatform.Application.Abstractions;

/// <summary>
/// Port for access-token creation. The implementation (Infrastructure) owns the signing key,
/// issuer/audience, and lifetime; Application only knows "a user becomes a signed token".
/// </summary>
public interface IJwtTokenGenerator
{
    (string AccessToken, DateTimeOffset ExpiresAt) Generate(User user);
}
