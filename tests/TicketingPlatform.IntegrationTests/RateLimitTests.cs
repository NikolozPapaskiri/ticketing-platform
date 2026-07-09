using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;

namespace TicketingPlatform.IntegrationTests;

/// <summary>Dedicated factory with a tiny auth rate limit so the window is easy to exhaust.</summary>
public sealed class TightRateLimitApiFactory : TicketingApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimiting:AuthRequestsPerMinute", "3");
    }
}

/// <summary>
/// Brute-force protection on the auth endpoints: requests beyond the per-IP window get 429
/// BEFORE any password hashing happens. Own containers because the shared factory deliberately
/// runs with the limit effectively off.
/// </summary>
public class RateLimitTests : IClassFixture<TightRateLimitApiFactory>
{
    private readonly HttpClient _client;

    public RateLimitTests(TightRateLimitApiFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task LoginFlood_GetsCutOffWith429()
    {
        var body = new { email = "attacker@example.com", password = "guess" };

        // Three allowed (all 401 - wrong creds), the fourth hits the window.
        for (var i = 0; i < 3; i++)
        {
            var allowed = await _client.PostAsJsonAsync("/api/v1/auth/login", body);
            Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
        }

        var blocked = await _client.PostAsJsonAsync("/api/v1/auth/login", body);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
    }
}
