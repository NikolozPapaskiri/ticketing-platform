using System.Net;
using System.Net.Http.Json;

namespace TicketingPlatform.IntegrationTests;

/// <summary>
/// The in-app ops snapshot endpoint: PlatformAdmin-only, and it returns a live, source-of-truth
/// view (dependency health + backlogs) regardless of which host role is asked.
/// </summary>
[Collection(nameof(ApiCollection))]
public class AdminOpsTests
{
    private readonly HttpClient _client;

    public AdminOpsTests(TicketingApiFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Ops_WithoutToken_Returns401()
    {
        var response = await _client.GetAsync("/api/v1/admin/ops");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Ops_AsCustomer_Returns403()
    {
        var (_, _, customerToken) = await _client.CreateCustomerAsync();

        var response = await _client.GetAsAsync(customerToken, "/api/v1/admin/ops");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Ops_AsAdmin_ReturnsLiveSnapshot()
    {
        var adminToken = await _client.LoginAsAdminAsync();

        var response = await _client.GetAsAsync(adminToken, "/api/v1/admin/ops");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var snapshot = await response.Content.ReadFromJsonAsync<OpsSnapshotDto>(ApiClientExtensions.Json);
        Assert.NotNull(snapshot);

        // Dependency health is reported for the real infrastructure.
        Assert.False(string.IsNullOrWhiteSpace(snapshot!.OverallStatus));
        var names = snapshot.Dependencies.Select(d => d.Name).ToList();
        Assert.Contains("postgres", names);
        Assert.Contains("redis", names);
        Assert.Contains("rabbitmq", names);

        // Backlog figures are present and non-negative (source-of-truth counts).
        Assert.True(snapshot.OutboxPending >= 0);
        Assert.True(snapshot.OutboxQuarantined >= 0);
        Assert.True(snapshot.WaitingRoomDepth >= 0);
        Assert.True(snapshot.PaymentsAwaitingReconciliation >= 0);
        Assert.NotNull(snapshot.OrdersByStatus);
    }
}

internal sealed record OpsSnapshotDto(
    DateTimeOffset GeneratedAt,
    string HostRole,
    string OverallStatus,
    List<OpsDependencyDto> Dependencies,
    long WaitingRoomDepth,
    long PaymentsAwaitingReconciliation,
    long RefundsPending,
    long OutboxPending,
    long OutboxQuarantined,
    double? OutboxOldestPendingAgeSeconds,
    long? DeadLetterDepth,
    Dictionary<string, long> OrdersByStatus);

internal sealed record OpsDependencyDto(string Name, string Status, string? Description);
