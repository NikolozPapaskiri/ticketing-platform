using System.Net.Http.Json;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// The hold-release race, factored out so it runs identically against every reservation strategy
/// (optimistic / pessimistic / redis) - the PR 2 gate requires "credits inventory at most once
/// under a release race" for all three. A SECOND live hold defeats the domain's clamp-at-capacity,
/// so a double credit is visible as availability that is too HIGH.
/// </summary>
internal static class ReleaseRaceScenario
{
    public static async Task AssertCreditsInventoryOnceAsync(PaymentRaceApiFactory factory, HttpClient client)
    {
        factory.Gateway.Reset();
        factory.Fault.Reset();

        const int capacity = 10, quantity = 2;
        var (_, staff) = await client.CreateTenantWithStaffAsync();
        var ev = await client.CreateEventAsync(staff);
        var ttResponse = await client.PostAsAsync(staff, $"/api/v1/events/{ev.Id}/ticket-types",
            new { name = "GA", price = 10m, currency = "USD", totalQuantity = capacity });
        var tt = (await ttResponse.Content.ReadFromJsonAsync<TicketTypeDto>(ApiClientExtensions.Json))!;

        var holdToRelease = await CreateStaffHoldAsync(client, staff, tt.Id, quantity);
        await CreateStaffHoldAsync(client, staff, tt.Id, quantity); // hold B stays active (available 6)

        // Barrier-free on purpose: the pessimistic strategy takes the inventory row lock inside
        // its transaction BEFORE the tracked save the interceptor gate hooks, so a save-time
        // barrier would deadlock it. Each strategy serializes the two releases its own way
        // (row lock / concurrency token / already-released check); the invariant is identical.
        var first = client.PostAsAsync(staff, $"/api/v1/holds/{holdToRelease}/release");
        var second = client.PostAsAsync(staff, $"/api/v1/holds/{holdToRelease}/release");
        await Task.WhenAll(first, second);

        // Releasing ONE hold of 2 must move availability 6 -> 8, never 6 -> 10.
        Assert.Equal(8, await AvailabilityAsync(client, staff, ev.Id, tt.Id));
    }

    private static async Task<Guid> CreateStaffHoldAsync(HttpClient client, string staff, Guid ticketTypeId, int quantity)
    {
        var response = await client.PostAsAsync(staff, "/api/v1/holds", new { ticketTypeId, quantity });
        response.EnsureSuccessStatusCode();
        var hold = (await response.Content.ReadFromJsonAsync<HoldDto>(ApiClientExtensions.Json))!;
        return hold.Id;
    }

    private static async Task<int> AvailabilityAsync(HttpClient client, string staff, Guid eventId, Guid ticketTypeId)
    {
        var graph = await (await client.GetAsAsync(staff, $"/api/v1/events/{eventId}"))
            .Content.ReadFromJsonAsync<EventDto>(ApiClientExtensions.Json);
        return graph!.TicketTypes.Single(t => t.Id == ticketTypeId).AvailableQuantity;
    }
}

/// <summary>Payment-race host pinned to the pessimistic (FOR UPDATE) reservation strategy.</summary>
public sealed class PessimisticReleaseApiFactory : PaymentRaceApiFactory
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Reservation:Strategy", "PessimisticLock");
    }
}

/// <summary>Payment-race host pinned to the Redis-atomic reservation strategy.</summary>
public sealed class RedisReleaseApiFactory : PaymentRaceApiFactory
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Reservation:Strategy", "RedisAtomic");
    }
}

[Collection(nameof(PessimisticReleaseCollection))]
public sealed class PessimisticReleaseRaceTests
{
    private readonly PessimisticReleaseApiFactory _factory;
    public PessimisticReleaseRaceTests(PessimisticReleaseApiFactory factory) => _factory = factory;

    [Fact]
    public Task ConcurrentRelease_SameHold_CreditsInventoryOnce() =>
        ReleaseRaceScenario.AssertCreditsInventoryOnceAsync(_factory, _factory.CreateClient());
}

[CollectionDefinition(nameof(PessimisticReleaseCollection))]
public sealed class PessimisticReleaseCollection : ICollectionFixture<PessimisticReleaseApiFactory>;

[Collection(nameof(RedisReleaseCollection))]
public sealed class RedisReleaseRaceTests
{
    private readonly RedisReleaseApiFactory _factory;
    public RedisReleaseRaceTests(RedisReleaseApiFactory factory) => _factory = factory;

    [Fact]
    public Task ConcurrentRelease_SameHold_CreditsInventoryOnce() =>
        ReleaseRaceScenario.AssertCreditsInventoryOnceAsync(_factory, _factory.CreateClient());
}

[CollectionDefinition(nameof(RedisReleaseCollection))]
public sealed class RedisReleaseCollection : ICollectionFixture<RedisReleaseApiFactory>;
