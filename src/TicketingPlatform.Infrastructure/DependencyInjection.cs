using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Infrastructure.Caching;
using TicketingPlatform.Infrastructure.Messaging;
using TicketingPlatform.Infrastructure.Outbox;
using TicketingPlatform.Infrastructure.Payments;
using TicketingPlatform.Infrastructure.Persistence;
using TicketingPlatform.Infrastructure.Persistence.Repositories;
using TicketingPlatform.Infrastructure.Reservations;
using TicketingPlatform.Infrastructure.Security;

namespace TicketingPlatform.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers Infrastructure services. The EF Core provider choice and the connection string
    /// live here, so the Api project does not reference Npgsql or know how persistence is wired.
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<TicketingDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("Default")));

        // Repositories: Application-defined ports, EF-backed implementations. Scoped, same as
        // the DbContext they wrap.
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<IEventRepository, EventRepository>();
        services.AddScoped<IHoldRepository, HoldRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IOrderRepository, OrderRepository>();

        // The outbox writer stages events through the caller's scoped DbContext (one transaction);
        // the dispatcher + consumer + expiry service run as hosted background services.
        services.AddScoped<IOutbox, OutboxWriter>();
        services.Configure<RabbitMqOptions>(configuration.GetSection(RabbitMqOptions.SectionName));
        services.AddHostedService<OutboxDispatcher>();
        services.AddHostedService<NotificationConsumer>();
        services.AddHostedService<HoldExpiryService>();

        // CQRS: the projection consumer maintains the read model; the query port serves it.
        services.AddHostedService<AvailabilityProjectionConsumer>();
        services.AddScoped<IAvailabilityReadModel, AvailabilityReadModel>();

        // Security: PBKDF2 hashing + JWT creation. Singletons - both are stateless.
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.AddSingleton<IPasswordHasherService, PasswordHasherService>();
        services.AddSingleton<IJwtTokenGenerator, JwtTokenGenerator>();

        // Payment provider: typed client via IHttpClientFactory (handler pooling, DNS-safe)
        // with the standard resilience pipeline (retry + backoff + jitter, circuit breaker,
        // timeouts). Retries are safe ONLY because ChargeAsync sends an Idempotency-Key.
        services.AddHttpClient<IPaymentGateway, PaymentProviderClient>(client =>
        {
            client.BaseAddress = new Uri(configuration["PaymentProvider:BaseUrl"]
                ?? throw new InvalidOperationException("Missing 'PaymentProvider:BaseUrl' configuration."));
        })
        .AddStandardResilienceHandler(options =>
        {
            // Base delay is configurable so tests can run the retry storm in milliseconds;
            // production keeps the 1s exponential backoff (1s, 2s, 4s) + built-in jitter.
            options.Retry.Delay = TimeSpan.FromMilliseconds(
                configuration.GetValue("PaymentProvider:RetryBaseDelayMs", 1000));
        });

        // Distributed cache: Redis. The cache is an optimization - RedisCacheService degrades
        // to DB reads if Redis is down.
        services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = configuration.GetConnectionString("Redis")
                ?? throw new InvalidOperationException("Missing 'ConnectionStrings:Redis' configuration.");
        });
        services.AddSingleton<ICacheService, RedisCacheService>();

        // Oversell prevention: pick ONE of the three strategies via configuration.
        // All three ship so they can be compared under load; switch by setting
        // "Reservation:Strategy" to "OptimisticConcurrency" | "PessimisticLock" | "RedisAtomic".
        var strategy = configuration["Reservation:Strategy"] ?? "OptimisticConcurrency";
        switch (strategy)
        {
            case "PessimisticLock":
                services.AddScoped<IReservationStrategy, PessimisticReservationStrategy>();
                break;
            case "RedisAtomic":
                // The raw multiplexer (DECRBY/INCRBY/SET NX) - IDistributedCache is too narrow.
                services.AddSingleton<IConnectionMultiplexer>(_ =>
                    ConnectionMultiplexer.Connect(configuration.GetConnectionString("Redis")!));
                services.AddScoped<IReservationStrategy, RedisAtomicReservationStrategy>();
                break;
            default:
                services.AddScoped<IReservationStrategy, OptimisticReservationStrategy>();
                break;
        }

        return services;
    }
}
