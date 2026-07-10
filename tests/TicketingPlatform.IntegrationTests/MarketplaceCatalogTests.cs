using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// The global cross-tenant public catalog (tkt.ge-style marketplace): anonymous browse of
/// every organizer's OnSale events with category/date/text filters, SQL-computed price-from,
/// and event images uploaded by staff and served anonymously.
/// Tests scope themselves with a unique name marker (the database is shared across the suite).
/// </summary>
[Collection(nameof(ApiCollection))]
public class MarketplaceCatalogTests
{
    private readonly HttpClient _client;

    public MarketplaceCatalogTests(TicketingApiFactory factory) => _client = factory.CreateClient();

    private static string Marker() => $"mkt{Guid.NewGuid():N}"[..12];

    private async Task<EventDto> CreateEventAsync(string staff, string name, string category, DateTimeOffset? startsAt = null)
    {
        var response = await _client.PostAsAsync(staff, "/api/v1/events",
            new { name, startsAt = startsAt ?? DateTimeOffset.UtcNow.AddMonths(1), category, venueName = "Main Hall" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EventDto>(ApiClientExtensions.Json))!;
    }

    private Task PublishAsync(string staff, Guid eventId) =>
        _client.PostAsAsync(staff, $"/api/v1/events/{eventId}/publish");

    private async Task<List<MarketplaceItemDto>> CatalogAsync(string query)
    {
        // Anonymous on purpose: the marketplace is the buyer-facing surface.
        var response = await _client.GetAsync($"/api/v1/public/events?{query}");
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<PageDto<MarketplaceItemDto>>(ApiClientExtensions.Json);
        return page!.Items.ToList();
    }

    [Fact]
    public async Task Catalog_IsCrossTenant_AndShowsOnlyOnSale()
    {
        var marker = Marker();
        var (_, staffA) = await _client.CreateTenantWithStaffAsync();
        var (_, staffB) = await _client.CreateTenantWithStaffAsync();

        var publishedA = await CreateEventAsync(staffA, $"{marker} Rock Night", "Concert");
        var publishedB = await CreateEventAsync(staffB, $"{marker} Derby", "Sport");
        await CreateEventAsync(staffA, $"{marker} Secret Draft", "Concert"); // never published
        await PublishAsync(staffA, publishedA.Id);
        await PublishAsync(staffB, publishedB.Id);

        var items = await CatalogAsync($"q={marker}");

        // One anonymous request sees BOTH tenants' events (the marketplace) but never drafts.
        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Id == publishedA.Id);
        Assert.Contains(items, i => i.Id == publishedB.Id);
        Assert.DoesNotContain(items, i => i.Name.Contains("Secret Draft"));
    }

    [Fact]
    public async Task Catalog_FiltersByCategoryAndDate()
    {
        var marker = Marker();
        var (_, staff) = await _client.CreateTenantWithStaffAsync();
        var soonConcert = await CreateEventAsync(staff, $"{marker} Concert Soon", "Concert", DateTimeOffset.UtcNow.AddDays(10));
        var lateSport = await CreateEventAsync(staff, $"{marker} Sport Late", "Sport", DateTimeOffset.UtcNow.AddDays(60));
        await PublishAsync(staff, soonConcert.Id);
        await PublishAsync(staff, lateSport.Id);

        var concerts = await CatalogAsync($"q={marker}&category=Concert");
        var item = Assert.Single(concerts);
        Assert.Equal(soonConcert.Id, item.Id);
        Assert.Equal("Concert", item.Category);

        // Date window that excludes the +60d event.
        var windowed = await CatalogAsync(
            $"q={marker}&from={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("o"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(30).ToString("o"))}");
        Assert.Equal(soonConcert.Id, Assert.Single(windowed).Id);
    }

    [Fact]
    public async Task Catalog_ComputesPriceFrom_InSql()
    {
        var marker = Marker();
        var (_, staff) = await _client.CreateTenantWithStaffAsync();
        var ev = await CreateEventAsync(staff, $"{marker} Priced", "Theatre");
        await _client.PostAsAsync(staff, $"/api/v1/events/{ev.Id}/ticket-types",
            new { name = "VIP", price = 55m, currency = "USD", totalQuantity = 10 });
        await _client.PostAsAsync(staff, $"/api/v1/events/{ev.Id}/ticket-types",
            new { name = "GA", price = 30m, currency = "USD", totalQuantity = 100 });
        var bare = await CreateEventAsync(staff, $"{marker} Bare", "Theatre");
        await PublishAsync(staff, ev.Id);
        await PublishAsync(staff, bare.Id);

        var items = await CatalogAsync($"q={marker}");

        Assert.Equal(30m, items.Single(i => i.Id == ev.Id).PriceFrom); // MIN over ticket types
        Assert.Null(items.Single(i => i.Id == bare.Id).PriceFrom);     // no ticket types yet
        Assert.Contains(items, i => i.TenantSlug.Length > 0);          // tenant identity on every card
    }

    [Fact]
    public async Task Detail_IsTenantAgnostic_AndHidesDrafts()
    {
        var (_, staff) = await _client.CreateTenantWithStaffAsync();
        var published = await CreateEventAsync(staff, "Detail Gala", "Opera");
        await _client.PostAsAsync(staff, $"/api/v1/events/{published.Id}/ticket-types",
            new { name = "GA", price = 20m, currency = "EUR", totalQuantity = 50 });
        await PublishAsync(staff, published.Id);
        var draft = await CreateEventAsync(staff, "Hidden Draft", "Opera");

        var ok = await _client.GetAsync($"/api/v1/public/events/{published.Id}");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var detail = await ok.Content.ReadFromJsonAsync<MarketplaceDetailDto>(ApiClientExtensions.Json);
        Assert.Equal("Opera", detail!.Category);
        Assert.False(string.IsNullOrEmpty(detail.TenantSlug));
        Assert.Single(detail.TicketTypes);

        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/v1/public/events/{draft.Id}")).StatusCode);
    }

    [Fact]
    public async Task Image_UploadByStaff_ServedAnonymously()
    {
        var (_, staff) = await _client.CreateTenantWithStaffAsync();
        var ev = await CreateEventAsync(staff, "Poster Show", "Festival");
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 }; // PNG-ish payload

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "poster.png");
        using var upload = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/events/{ev.Id}/image") { Content = form };
        upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staff);
        var uploaded = await _client.SendAsync(upload);
        Assert.Equal(HttpStatusCode.NoContent, uploaded.StatusCode);

        await PublishAsync(staff, ev.Id);

        // Served anonymously for catalog cards, with the uploaded bytes and content type.
        var image = await _client.GetAsync($"/api/v1/public/events/{ev.Id}/image");
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/png", image.Content.Headers.ContentType!.MediaType);
        Assert.Equal(bytes, await image.Content.ReadAsByteArrayAsync());

        // And the card flag flips.
        var detail = await (await _client.GetAsync($"/api/v1/public/events/{ev.Id}"))
            .Content.ReadFromJsonAsync<MarketplaceDetailDto>(ApiClientExtensions.Json);
        Assert.True(detail!.HasImage);
    }

    [Fact]
    public async Task Image_RejectsWrongTypeAndForeignEvent()
    {
        var (_, staffA) = await _client.CreateTenantWithStaffAsync();
        var (_, staffB) = await _client.CreateTenantWithStaffAsync();
        var ev = await CreateEventAsync(staffA, "Guarded", "Kids");

        async Task<HttpStatusCode> UploadAsync(string token, string contentType)
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent([1, 2, 3]);
            file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(file, "file", "x.bin");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/events/{ev.Id}/image") { Content = form };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return (await _client.SendAsync(request)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.BadRequest, await UploadAsync(staffA, "application/pdf")); // wrong type
        Assert.Equal(HttpStatusCode.NotFound, await UploadAsync(staffB, "image/png"));         // foreign tenant
    }

    private sealed record MarketplaceItemDto(
        Guid Id, string Name, string? VenueName, DateTimeOffset StartsAt, string Category,
        string TenantName, string TenantSlug, decimal? PriceFrom, string? Currency, bool HasImage);

    private sealed record MarketplaceDetailDto(
        Guid Id, string Name, string? Description, string? VenueName, DateTimeOffset StartsAt,
        string Category, string TenantName, string TenantSlug, bool HasImage, List<TicketTypeDto> TicketTypes);
}
