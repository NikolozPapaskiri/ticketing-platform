using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketingPlatform.Domain;
using TicketingPlatform.Infrastructure.Persistence;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// Phase A slice 3, the migrate step - the READ half. Every one of these deliberately drives the
/// performance's date AWAY from the legacy Event.StartsAt column and then asks the API what date
/// the event is on. If any surface still read the column, it would answer with the stale one.
/// That divergence cannot happen through the API (the write path keeps the two in step), which is
/// exactly why it is the thing to test: it is the only way to tell which column was read.
/// </summary>
[Collection(nameof(ApiCollection))]
public class PerformanceReadPathTests
{
    private readonly TicketingApiFactory _factory;
    private readonly HttpClient _client;

    public PerformanceReadPathTests(TicketingApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static string Marker() => $"perf{Guid.NewGuid():N}"[..14];

    [Fact]
    public async Task EverySurfaceShowsThePerformancesDate_NotTheLegacyColumn()
    {
        var marker = Marker();
        var (tenant, staff) = await _client.CreateTenantWithStaffAsync();
        var ev = await CreateAndPublishAsync(staff, marker);

        // The real date moves; the legacy column is left pointing at the old one.
        var realDate = DateTimeOffset.UtcNow.AddMonths(7).AddMinutes(-DateTimeOffset.UtcNow.Minute);
        await MoveThePerformanceAsync(tenant.Id, ev.Id, realDate);

        // 1. the staff event graph
        var graph = await GetAsync<EventDto>(staff, $"/api/v1/events/{ev.Id}");
        Assert.Equal(realDate.ToUnixTimeSeconds(), graph.StartsAt.ToUnixTimeSeconds());

        // 2. the staff event list
        var list = await GetAsync<PageDto<DatedListItemDto>>(staff, "/api/v1/events?pageSize=100");
        var listed = Assert.Single(list.Items, i => i.Id == ev.Id);
        Assert.Equal(realDate.ToUnixTimeSeconds(), listed.StartsAt.ToUnixTimeSeconds());

        // 3. the marketplace catalog
        var catalog = await GetAnonymousAsync<PageDto<DatedListItemDto>>($"/api/v1/public/events?q={marker}");
        var card = Assert.Single(catalog.Items);
        Assert.Equal(realDate.ToUnixTimeSeconds(), card.StartsAt.ToUnixTimeSeconds());

        // 4. the marketplace event page
        var detail = await GetAnonymousAsync<DatedListItemDto>($"/api/v1/public/events/{ev.Id}");
        Assert.Equal(realDate.ToUnixTimeSeconds(), detail.StartsAt.ToUnixTimeSeconds());

        // 5. the organizer's own storefront
        var storefront = await GetAnonymousAsync<DatedListItemDto>(
            $"/api/v1/public/tenants/{tenant.Slug}/events/{ev.Id}");
        Assert.Equal(realDate.ToUnixTimeSeconds(), storefront.StartsAt.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task TheCatalogsDateFilter_FollowsThePerformance()
    {
        var marker = Marker();
        var (tenant, staff) = await _client.CreateTenantWithStaffAsync();
        var ev = await CreateAndPublishAsync(staff, marker);

        // Created a month out, then moved a year out. A "next three months" search must lose it.
        var movedFarOut = DateTimeOffset.UtcNow.AddYears(1);
        await MoveThePerformanceAsync(tenant.Id, ev.Id, movedFarOut);

        var soon = DateTimeOffset.UtcNow.AddMonths(3);
        var nearTerm = await GetAnonymousAsync<PageDto<DatedListItemDto>>(
            $"/api/v1/public/events?q={marker}&to={Uri.EscapeDataString(soon.ToString("O"))}");
        Assert.Empty(nearTerm.Items);

        var from = movedFarOut.AddDays(-1);
        var farOut = await GetAnonymousAsync<PageDto<DatedListItemDto>>(
            $"/api/v1/public/events?q={marker}&from={Uri.EscapeDataString(from.ToString("O"))}");
        Assert.Single(farOut.Items);
    }

    [Fact]
    public async Task ACancelledDate_NeverBecomesTheHeadlineDate()
    {
        var marker = Marker();
        var (tenant, staff) = await _client.CreateTenantWithStaffAsync();
        var ev = await CreateAndPublishAsync(staff, marker);

        var secondNight = DateTimeOffset.UtcNow.AddMonths(4);
        using (var scope = TenantScope(tenant.Id))
        {
            var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
            db.Performances.Add(new Performance
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                EventId = ev.Id,
                StartsAt = secondNight,
                CreatedAt = DateTimeOffset.UtcNow
            });
            // Call off the earlier night. Its sibling keeps selling - that is what the split buys.
            var first = await db.Performances.FirstAsync(p => p.EventId == ev.Id);
            first.Cancel(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        // The event must advertise the night it is still playing, not the one it called off.
        var detail = await GetAnonymousAsync<DatedListItemDto>($"/api/v1/public/events/{ev.Id}");
        Assert.Equal(secondNight.ToUnixTimeSeconds(), detail.StartsAt.ToUnixTimeSeconds());
    }

    private async Task<EventDto> CreateAndPublishAsync(string staff, string marker)
    {
        var response = await _client.PostAsAsync(staff, "/api/v1/events",
            new { name = $"{marker} Show", startsAt = DateTimeOffset.UtcNow.AddMonths(1), venueName = "Main Hall" });
        response.EnsureSuccessStatusCode();
        var ev = (await response.Content.ReadFromJsonAsync<EventDto>(ApiClientExtensions.Json))!;
        (await _client.PostAsAsync(staff, $"/api/v1/events/{ev.Id}/publish")).EnsureSuccessStatusCode();
        return ev;
    }

    /// <summary>Moves the date row only, straight in the database - the API keeps the two in step.</summary>
    private async Task MoveThePerformanceAsync(Guid tenantId, Guid eventId, DateTimeOffset startsAt)
    {
        using var scope = TenantScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
        var performance = await db.Performances.SingleAsync(p => p.EventId == eventId);
        performance.Reschedule(startsAt);
        await db.SaveChangesAsync();
    }

    private IServiceScope TenantScope(Guid tenantId)
    {
        var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TicketingPlatform.Api.Tenancy.TenantContext>().SetTenant(tenantId);
        return scope;
    }

    private async Task<T> GetAsync<T>(string token, string url)
    {
        var response = await _client.GetAsAsync(token, url);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(ApiClientExtensions.Json))!;
    }

    private async Task<T> GetAnonymousAsync<T>(string url)
    {
        var response = await _client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(ApiClientExtensions.Json))!;
    }

    /// <summary>Only the two fields these tests assert on; every listed shape carries both.</summary>
    private sealed record DatedListItemDto(Guid Id, DateTimeOffset StartsAt);
}
