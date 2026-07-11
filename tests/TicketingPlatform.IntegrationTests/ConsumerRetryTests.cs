using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using TicketingPlatform.Infrastructure.Messaging;
using TicketingPlatform.Infrastructure.Outbox;
using TicketingPlatform.Infrastructure.Persistence;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace TicketingPlatform.IntegrationTests;

/// <summary>PR 3: transient failures retry with a bound; malformed payloads park immediately.</summary>
[Collection(nameof(ApiCollection))]
public sealed class ConsumerRetryTests
{
    private readonly TicketingApiFactory _factory;
    private readonly HttpClient _client;

    public ConsumerRetryTests(TicketingApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Consumer_TransientFailure_RetriesThenSucceeds()
    {
        var storage = _factory.Services.GetRequiredService<TransientFileStorage>();
        storage.FailNextSaves(1);

        var (staff, hold) = await SetupHoldAsync();
        var response = await _client.PostAsAsync(staff, "/api/v1/orders",
            new { holdId = hold.Id, customerEmail = "retry-ticket@example.com" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = await response.Content.ReadFromJsonAsync<OrderDto>(ApiClientExtensions.Json);

        var issued = await WaitForTicketAsync(order!.Id);
        Assert.True(issued, "ticket issuer did not recover from one transient storage failure");
        Assert.Equal(2, storage.SaveAttempts);
    }

    [Fact]
    public async Task Consumer_TransientFailure_ExhaustsAttemptBudgetThenDeadLetters()
    {
        await PurgeAsync(RabbitMqTopology.DeadLetterQueue);
        var storage = _factory.Services.GetRequiredService<TransientFileStorage>();
        storage.FailNextSaves(10);

        var (staff, hold) = await SetupHoldAsync();
        var response = await _client.PostAsAsync(staff, "/api/v1/orders",
            new { holdId = hold.Id, customerEmail = "failed-ticket@example.com" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var parked = await WaitForQueueCountAsync(RabbitMqTopology.DeadLetterQueue, minimum: 1);
        Assert.True(parked, "exhausted ticket message was not parked in the dead-letter queue");
        Assert.Equal(3, storage.SaveAttempts);
    }

    [Fact]
    public async Task Consumer_InvalidPayload_DeadLettersWithoutRetryLoop()
    {
        await PurgeAsync(RabbitMqTopology.DeadLetterQueue);
        var probeId = Guid.NewGuid();
        await WithDbAsync(async db =>
        {
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = probeId,
                Type = "AvailabilityChanged",
                Payload = "{not-json",
                OccurredAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        });

        var parked = await WaitForQueueCountAsync(RabbitMqTopology.DeadLetterQueue, minimum: 1);
        Assert.True(parked, "malformed payload was not parked in the dead-letter queue");

        var firstCount = await GetQueueCountAsync(RabbitMqTopology.DeadLetterQueue);
        await Task.Delay(TimeSpan.FromSeconds(2));
        var finalCount = await GetQueueCountAsync(RabbitMqTopology.DeadLetterQueue);
        Assert.Equal(firstCount, finalCount);

        await WithDbAsync(db => db.OutboxMessages.Where(m => m.Id == probeId).ExecuteDeleteAsync());
    }

    private async Task<(string Staff, HoldDto Hold)> SetupHoldAsync()
    {
        _factory.PaymentProvider.Reset();
        _factory.PaymentProvider
            .Given(Request.Create().WithPath("/charges").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { chargeId = $"ch_{Guid.NewGuid():N}" }));

        var (_, staff) = await _client.CreateTenantWithStaffAsync();
        var ev = await _client.CreateEventAsync(staff);
        var ticketTypeResponse = await _client.PostAsAsync(staff, $"/api/v1/events/{ev.Id}/ticket-types",
            new { name = "GA", price = 25m, currency = "USD", totalQuantity = 10 });
        var ticketType = (await ticketTypeResponse.Content.ReadFromJsonAsync<TicketTypeDto>(ApiClientExtensions.Json))!;
        var holdResponse = await _client.PostAsAsync(staff, "/api/v1/holds",
            new { ticketTypeId = ticketType.Id, quantity = 1 });
        var hold = (await holdResponse.Content.ReadFromJsonAsync<HoldDto>(ApiClientExtensions.Json))!;
        return (staff, hold);
    }

    private async Task<bool> WaitForTicketAsync(Guid orderId)
    {
        for (var i = 0; i < 60; i++)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
            if (await db.Tickets.IgnoreQueryFilters().AnyAsync(t => t.OrderId == orderId))
                return true;
            await Task.Delay(250);
        }
        return false;
    }

    private async Task<bool> WaitForQueueCountAsync(string queue, uint minimum)
    {
        for (var i = 0; i < 40; i++)
        {
            if (await GetQueueCountAsync(queue) >= minimum)
                return true;
            await Task.Delay(250);
        }
        return false;
    }

    private async Task<uint> GetQueueCountAsync(string queue)
    {
        await using var connection = await CreateRabbitConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        var result = await channel.QueueDeclarePassiveAsync(queue);
        return result.MessageCount;
    }

    private async Task PurgeAsync(string queue)
    {
        await using var connection = await CreateRabbitConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueuePurgeAsync(queue);
    }

    private async Task<IConnection> CreateRabbitConnectionAsync()
    {
        var factory = new ConnectionFactory
        {
            HostName = _factory.RabbitHost,
            Port = _factory.RabbitPort,
            UserName = "ticketing",
            Password = "ticketing"
        };
        return await factory.CreateConnectionAsync();
    }

    private async Task WithDbAsync(Func<TicketingDbContext, Task> action)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
        await action(db);
    }
}
