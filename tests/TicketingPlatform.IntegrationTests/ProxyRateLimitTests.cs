using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// TestServer has no real socket, so <c>Connection.RemoteIpAddress</c> is null and the forwarded-
/// headers middleware has no trusted peer to evaluate. This front middleware stamps a fixed peer
/// IP (the stand-in "proxy") before anything else runs, so the trust decision is exercised exactly
/// as it would be behind a real ingress.
/// </summary>
internal sealed class RemoteIpStartupFilter : IStartupFilter
{
    private readonly IPAddress _peer;
    public RemoteIpStartupFilter(IPAddress peer) => _peer = peer;

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            context.Connection.RemoteIpAddress = _peer;
            await nextMiddleware();
        });
        next(app);
    };
}

/// <summary>Trusts loopback as a proxy, so X-Forwarded-For becomes the rate-limit partition key.</summary>
public sealed class ProxyTrustedRateLimitApiFactory : TicketingApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimiting:AuthRequestsPerMinute", "3");
        builder.UseSetting("ReverseProxy:Enabled", "true");
        builder.UseSetting("ReverseProxy:KnownNetworks:0", "127.0.0.1/32");
        builder.UseSetting("ReverseProxy:KnownNetworks:1", "::1/128");
        builder.ConfigureTestServices(services =>
            services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(IPAddress.Loopback)));
    }
}

/// <summary>No trusted proxy configured, so X-Forwarded-For must be ignored entirely.</summary>
public sealed class ProxyUntrustedRateLimitApiFactory : TicketingApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimiting:AuthRequestsPerMinute", "3");
        builder.UseSetting("ReverseProxy:Enabled", "false");
        builder.ConfigureTestServices(services =>
            services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(IPAddress.Loopback)));
    }
}

/// <summary>
/// PR 5 - proxy-aware rate limiting. Behind a trusted proxy the per-IP brute-force window must key
/// on the forwarded client, not the shared ingress address; with no proxy configured the same
/// header must be ignored so a client cannot forge its way into a fresh window.
/// </summary>
public class ProxyRateLimitTests
{
    private static HttpRequestMessage Login(string forwardedFor)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { email = "attacker@example.com", password = "guess" })
        };
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        return request;
    }

    [Fact]
    public async Task RateLimiter_UsesTrustedForwardedClientIp()
    {
        await using var factory = new ProxyTrustedRateLimitApiFactory();
        await ((IAsyncLifetime)factory).InitializeAsync();
        try
        {
            var client = factory.CreateClient();

            // One forwarded client exhausts ITS window (3 allowed => 401, 4th => 429).
            for (var i = 0; i < 3; i++)
            {
                var allowed = await client.SendAsync(Login("203.0.113.10"));
                Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
            }
            var blocked = await client.SendAsync(Login("203.0.113.10"));
            Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);

            // A DIFFERENT forwarded client still has its own window: the header really is the key.
            var otherClient = await client.SendAsync(Login("203.0.113.20"));
            Assert.Equal(HttpStatusCode.Unauthorized, otherClient.StatusCode);
        }
        finally
        {
            await ((IAsyncLifetime)factory).DisposeAsync();
        }
    }

    [Fact]
    public async Task RateLimiter_DoesNotTrustUnconfiguredProxyHeaders()
    {
        await using var factory = new ProxyUntrustedRateLimitApiFactory();
        await ((IAsyncLifetime)factory).InitializeAsync();
        try
        {
            var client = factory.CreateClient();

            // With no trusted proxy, every request shares the socket-peer partition regardless of
            // the header, so spoofing a fresh X-Forwarded-For per request cannot dodge the window.
            for (var i = 0; i < 3; i++)
            {
                var allowed = await client.SendAsync(Login($"203.0.113.{i}"));
                Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
            }

            var blocked = await client.SendAsync(Login("203.0.113.99"));
            Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        }
        finally
        {
            await ((IAsyncLifetime)factory).DisposeAsync();
        }
    }
}
