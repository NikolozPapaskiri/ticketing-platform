using System.Text;
using TicketingPlatform.Infrastructure.Security;

namespace TicketingPlatform.Api.Common;

/// <summary>
/// Fail-fast validation of security-sensitive configuration at startup. A misconfigured signing
/// key or a leftover development secret in production is a silent authentication hole; better a
/// process that refuses to start than one that mints forgeable-in-practice tokens.
/// </summary>
public static class SecurityOptionsValidation
{
    // HMAC-SHA256 keys shorter than the 256-bit output add no security and Microsoft's handler
    // will reject them at sign time - catch it at boot instead of on the first login.
    private const int MinSigningKeyBytes = 32;
    private const string DevKeyMarker = "DEV-ONLY";

    public static void ValidateJwt(JwtOptions jwt, bool isDevelopment)
    {
        if (string.IsNullOrWhiteSpace(jwt.Issuer))
            throw new InvalidOperationException("Jwt:Issuer must be configured.");

        if (string.IsNullOrWhiteSpace(jwt.Audience))
            throw new InvalidOperationException("Jwt:Audience must be configured.");

        if (string.IsNullOrEmpty(jwt.SigningKey) || Encoding.UTF8.GetByteCount(jwt.SigningKey) < MinSigningKeyBytes)
            throw new InvalidOperationException(
                $"Jwt:SigningKey must be at least {MinSigningKeyBytes} bytes (256 bits) for HMAC-SHA256.");

        if (!isDevelopment && jwt.SigningKey.Contains(DevKeyMarker, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Jwt:SigningKey is the development placeholder; supply a real secret outside Development.");

        if (jwt.AccessTokenMinutes <= 0)
            throw new InvalidOperationException("Jwt:AccessTokenMinutes must be greater than zero.");
    }
}
