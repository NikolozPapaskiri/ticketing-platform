using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;

namespace TicketingPlatform.IntegrationTests;

/// <summary>Dedicated factory with a 2-second hold TTL and a 1-second expiry scan.</summary>
public sealed class ShortTtlApiFactory : TicketingApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Holds:TtlSeconds", "2");
        builder.UseSetting("Holds:ExpiryScanSeconds", "1");
    }
}

/// <summary>
/// The saga's compensation: an abandoned hold expires and its inventory flows back without
/// any human intervention. Runs on its OWN containers (short TTL would make every other hold
/// test racy), so it is the one deliberately expensive test class.
/// </summary>
public class ExpiryTests : IClassFixture<ShortTtlApiFactory>
{
    private readonly HttpClient _client;

    public ExpiryTests(ShortTtlApiFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task AbandonedHold_Expires_AndInventoryFlowsBack()
    {
        var (_, staff) = await _client.CreateTenantWithStaffAsync();
        var ev = await _client.CreateEventAsync(staff);
        var ttResponse = await _client.PostAsAsync(staff, $"/api/v1/events/{ev.Id}/ticket-types",
            new { name = "GA", price = 10m, currency = "USD", totalQuantity = 10 });
        var tt = (await ttResponse.Content.ReadFromJsonAsync<TicketTypeDto>(ApiClientExtensions.Json))!;

        var holdResponse = await _client.PostAsAsync(staff, "/api/v1/holds",
            new { ticketTypeId = tt.Id, quantity = 4 });
        var hold = (await holdResponse.Content.ReadFromJsonAsync<HoldDto>(ApiClientExtensions.Json))!;

        // Reserved: 6 left. Now abandon it and let the expiry service do its job (TTL 2s, scan 1s).
        var restored = false;
        for (var i = 0; i < 80 && !restored; i++)
        {
            await Task.Delay(250);
            var graph = await (await _client.GetAsAsync(staff, $"/api/v1/events/{ev.Id}"))
                .Content.ReadFromJsonAsync<EventDto>(ApiClientExtensions.Json);
            restored = graph!.TicketTypes.Single().AvailableQuantity == 10;
        }
        Assert.True(restored, "expired hold never released its inventory within 20s");

        var holdRead = await (await _client.GetAsAsync(staff, $"/api/v1/holds/{hold.Id}"))
            .Content.ReadFromJsonAsync<HoldDto>(ApiClientExtensions.Json);
        Assert.Equal("Expired", holdRead!.Status);
    }
}
