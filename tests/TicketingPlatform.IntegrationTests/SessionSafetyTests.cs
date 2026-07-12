using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketingPlatform.Infrastructure.Persistence;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// Host with a real (but short) rotation grace window so the concurrency, within-grace, and
/// outside-grace refresh behaviours can all be exercised deterministically. Own containers - the
/// shared factory deliberately runs with the grace window disabled.
/// </summary>
public sealed class SessionSafetyApiFactory : TicketingApiFactory
{
    public const int GraceSeconds = 2;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Auth:RefreshRotationGraceSeconds", GraceSeconds.ToString());
    }
}

[CollectionDefinition(nameof(SessionSafetyCollection))]
public sealed class SessionSafetyCollection : ICollectionFixture<SessionSafetyApiFactory>;

/// <summary>
/// PR 5 - refresh-session concurrency. Parallel refreshes of one session must not be mistaken for
/// token theft or fork the session into independent families; logout must revoke server-side; and
/// a genuinely replayed (out-of-grace) token must still burn the whole family.
/// </summary>
[Collection(nameof(SessionSafetyCollection))]
public sealed class SessionSafetyTests
{
    private readonly SessionSafetyApiFactory _factory;
    private readonly HttpClient _client;

    public SessionSafetyTests(SessionSafetyApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ConcurrentRefresh_SameSession_DoesNotForkOrRevokeLegitimateSession()
    {
        var (userId, pair) = await RegisterAndLoginAsync();

        // Fire several refreshes of the SAME token at once: one wins the atomic rotation, the
        // rest land in the grace window as legitimate concurrent refreshes.
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            _client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = pair.RefreshToken })));

        // No parallel request is treated as theft.
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        var rotated = await Task.WhenAll(responses.Select(async r =>
            (await r.Content.ReadFromJsonAsync<AuthDto>(ApiClientExtensions.Json))!));

        // Every issued token stays in ONE family - the session did not fork into independent chains.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
            var families = await db.RefreshTokens
                .Where(t => t.UserId == userId)
                .Select(t => t.FamilyId)
                .Distinct()
                .ToListAsync();
            Assert.Single(families);
        }

        // And the session is alive: a returned token still refreshes.
        var followUp = await _client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = rotated[0].RefreshToken });
        Assert.Equal(HttpStatusCode.OK, followUp.StatusCode);
    }

    [Fact]
    public async Task Refresh_ReplayWithinGrace_IsToleratedAsConcurrentRefresh()
    {
        var (_, pair1) = await RegisterAndLoginAsync();

        var refresh = await _client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = pair1.RefreshToken });
        var pair2 = (await refresh.Content.ReadFromJsonAsync<AuthDto>(ApiClientExtensions.Json))!;

        // Replaying the just-rotated token inside the grace window is a legitimate near-concurrent
        // refresh, not theft: it succeeds and does NOT revoke the family.
        var withinGrace = await _client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = pair1.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, withinGrace.StatusCode);

        var pair2StillWorks = await _client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = pair2.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, pair2StillWorks.StatusCode);
    }

    [Fact]
    public async Task Refresh_ReplayOutsideGrace_RevokesSessionFamily()
    {
        var (_, pair1) = await RegisterAndLoginAsync();

        var refresh = await _client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = pair1.RefreshToken });
        var pair2 = (await refresh.Content.ReadFromJsonAsync<AuthDto>(ApiClientExtensions.Json))!;

        // Wait out the grace window: now the old token is a genuine replay/theft signal.
        await Task.Delay(TimeSpan.FromSeconds(SessionSafetyApiFactory.GraceSeconds) + TimeSpan.FromMilliseconds(400));

        var replay = await _client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = pair1.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // The family is burned - even the once-valid newer token dies with it.
        var family = await _client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = pair2.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, family.StatusCode);
    }

    [Fact]
    public async Task Logout_RevokesRefreshTokenServerSide()
    {
        var (_, pair) = await RegisterAndLoginAsync();

        var logout = await _client.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = pair.RefreshToken });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        // The refresh token is dead server-side: clearing the cookie was not the only defence.
        var afterLogout = await _client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = pair.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    [Fact]
    public async Task Logout_UnknownToken_IsIdempotentNoContent()
    {
        // Logout must not become an oracle for token validity: garbage still returns 204.
        var logout = await _client.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = "not-a-real-token" });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
    }

    private async Task<(Guid UserId, AuthDto Pair)> RegisterAndLoginAsync()
    {
        var email = $"s-{Guid.NewGuid():N}@test.local";
        const string password = "Secret123$";

        var register = await _client.PostAsJsonAsync("/api/v1/auth/register", new { email, password });
        var user = (await register.Content.ReadFromJsonAsync<UserDto>(ApiClientExtensions.Json))!;

        var login = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        var pair = (await login.Content.ReadFromJsonAsync<AuthDto>(ApiClientExtensions.Json))!;

        return (user.Id, pair);
    }
}
