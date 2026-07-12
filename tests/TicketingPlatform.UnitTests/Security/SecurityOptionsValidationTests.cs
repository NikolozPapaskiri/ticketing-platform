using TicketingPlatform.Api.Common;
using TicketingPlatform.Infrastructure.Security;

namespace TicketingPlatform.UnitTests.Security;

/// <summary>
/// PR 5 - security-sensitive options are validated at startup, so a weak/missing signing key or a
/// leftover development secret in production stops the process instead of silently minting
/// forgeable-in-practice tokens.
/// </summary>
public class SecurityOptionsValidationTests
{
    private const string DevKey = "DEV-ONLY-signing-key-ticketing-platform-local-0123456789";

    private static JwtOptions Build(
        string? signingKey = null,
        string issuer = "ticketing-platform",
        string audience = "ticketing-api",
        int minutes = 15) => new()
    {
        Issuer = issuer,
        Audience = audience,
        SigningKey = signingKey ?? new string('k', 40),
        AccessTokenMinutes = minutes
    };

    [Fact]
    public void ValidProductionOptions_DoNotThrow() =>
        SecurityOptionsValidation.ValidateJwt(Build(), isDevelopment: false);

    [Theory]
    [InlineData("")]                       // missing entirely
    [InlineData("too-short-key")]          // < 32 bytes: no security, handler rejects it anyway
    public void WeakOrMissingSigningKey_Throws(string key) =>
        Assert.Throws<InvalidOperationException>(() =>
            SecurityOptionsValidation.ValidateJwt(Build(signingKey: key), isDevelopment: false));

    [Fact]
    public void DevelopmentPlaceholderKey_InProduction_Throws() =>
        Assert.Throws<InvalidOperationException>(() =>
            SecurityOptionsValidation.ValidateJwt(Build(signingKey: DevKey), isDevelopment: false));

    [Fact]
    public void DevelopmentPlaceholderKey_InDevelopment_IsAllowed() =>
        SecurityOptionsValidation.ValidateJwt(Build(signingKey: DevKey), isDevelopment: true);

    [Fact]
    public void MissingIssuer_Throws() =>
        Assert.Throws<InvalidOperationException>(() =>
            SecurityOptionsValidation.ValidateJwt(Build(issuer: " "), isDevelopment: false));

    [Fact]
    public void MissingAudience_Throws() =>
        Assert.Throws<InvalidOperationException>(() =>
            SecurityOptionsValidation.ValidateJwt(Build(audience: ""), isDevelopment: false));

    [Fact]
    public void NonPositiveAccessTokenMinutes_Throws() =>
        Assert.Throws<InvalidOperationException>(() =>
            SecurityOptionsValidation.ValidateJwt(Build(minutes: 0), isDevelopment: false));
}
