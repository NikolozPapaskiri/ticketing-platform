using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Domain;

namespace TicketingPlatform.Infrastructure.Security;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public required string Issuer { get; init; }
    public required string Audience { get; init; }

    /// <summary>HMAC-SHA256 key, >= 32 bytes. Dev value lives in appsettings.Development.json
    /// (clearly labeled); production supplies it via environment/secret store.</summary>
    public required string SigningKey { get; init; }

    /// <summary>Short on purpose: a stateless JWT cannot be revoked, so its blast radius is its lifetime.</summary>
    public int AccessTokenMinutes { get; init; } = 15;
}

/// <summary>
/// Creates signed access tokens. A JWT is SIGNED, NOT ENCRYPTED: anyone can read the payload,
/// the signature only proves it was not tampered with - so no secrets ever go in claims.
/// </summary>
public sealed class JwtTokenGenerator : IJwtTokenGenerator
{
    private readonly JwtOptions _options;
    private readonly TimeProvider _clock;

    public JwtTokenGenerator(IOptions<JwtOptions> options, TimeProvider clock)
    {
        _options = options.Value;
        _clock = clock;
    }

    public (string AccessToken, DateTimeOffset ExpiresAt) Generate(User user)
    {
        var now = _clock.GetUtcNow();
        var expiresAt = now.AddMinutes(_options.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()), // unique token id
            new("role", user.Role.ToString())
        };

        // THE multi-tenancy move of Phase 3: staff carry their tenant in the token, signed by
        // the server. The API trusts this claim instead of a client-supplied header, so a
        // client can no longer choose its own tenant.
        if (user.TenantId is not null)
            claims.Add(new Claim("tenant_id", user.TenantId.Value.ToString()));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
