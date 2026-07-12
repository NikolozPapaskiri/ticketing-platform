using Amazon.Runtime;
using Amazon.S3;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Application.Common;
using TicketingPlatform.Infrastructure.Caching;
using TicketingPlatform.Infrastructure.Documents;
using TicketingPlatform.Infrastructure.Messaging;
using TicketingPlatform.Infrastructure.Outbox;
using TicketingPlatform.Infrastructure.Payments;
using TicketingPlatform.Infrastructure.Storage;
using TicketingPlatform.Infrastructure.Persistence;
using TicketingPlatform.Infrastructure.Persistence.Repositories;
using TicketingPlatform.Infrastructure.Reservations;
using TicketingPlatform.Infrastructure.Security;
using TicketingPlatform.Infrastructure.WaitingRoom;

namespace TicketingPlatform.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers Infrastructure services. The EF Core provider choice and the connection string
    /// live here, so the Api project does not reference Npgsql or know how persistence is wired.
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Same image, different profile: the background workers run only on a role that runs
        // workers (All/Worker), so scaling the API's HTTP replicas cannot multiply admission
        // valves or scheduled jobs. API-only pods register none of them.
        var role = HostRoleExtensions.ParseHostRole(configuration["Hosting:Role"]);
        var runsWorkers = role.RunsWorkers();

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
        // it runs on the API too (checkout writes rows). The dispatcher/consumers that PUBLISH and
        // CONSUME from the broker are worker-only.
        services.AddScoped<IOutbox, OutboxWriter>();
        services.Configure<RabbitMqOptions>(configuration.GetSection(RabbitMqOptions.SectionName));
        services.AddSingleton<RabbitMqOutboxPublisher>();
        services.AddSingleton<IOutboxPublisher>(sp => sp.GetRequiredService<RabbitMqOutboxPublisher>());
        if (runsWorkers)
        {
            // Declare the broker topology BEFORE the dispatcher/consumers start, so no publish can
            // race ahead of its bindings. Registered first => its StartAsync completes first.
            services.AddHostedService<RabbitMqTopologyInitializer>();
            services.AddHostedService<OutboxDispatcher>();
            services.AddHostedService<NotificationConsumer>();
            services.AddHostedService<HoldExpiryService>();
            services.AddHostedService<PaymentReconciliationService>();
            // Retention sweep for the unbounded bookkeeping tables (outbox, dedupe, idempotency,
            // dead refresh tokens). Worker-only scheduled work.
            services.AddHostedService<RetentionService>();
        }

        // CQRS: the projection consumer maintains the read model (worker-only); the query port
        // serves it (registered everywhere - the API reads the read model).
        if (runsWorkers)
            services.AddHostedService<AvailabilityProjectionConsumer>();
        services.AddScoped<IAvailabilityReadModel, AvailabilityReadModel>();

        // Ticket issuing: PDF generation + file storage behind ports. The consumer is worker-only;
        // the ports stay registered everywhere (the API streams stored PDFs to clients).
        services.AddSingleton<ITicketDocumentGenerator, QuestPdfTicketGenerator>();
        AddFileStorage(services, configuration);
        if (runsWorkers)
            services.AddHostedService<TicketIssuerConsumer>();

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
        // Raw multiplexer (ZADD/ZPOPMIN/DECRBY...) - IDistributedCache is too narrow for the
        // waiting room and the Redis reservation strategy. One shared connection, lazily opened.
        services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(configuration.GetConnectionString("Redis")!));

        var strategy = configuration["Reservation:Strategy"] ?? "OptimisticConcurrency";
        switch (strategy)
        {
            case "PessimisticLock":
                services.AddScoped<IReservationStrategy, PessimisticReservationStrategy>();
                break;
            case "RedisAtomic":
                services.AddScoped<IReservationStrategy, RedisAtomicReservationStrategy>();
                break;
            default:
                services.AddScoped<IReservationStrategy, OptimisticReservationStrategy>();
                break;
        }

        // Virtual waiting room: Redis-backed line (read/written by the API) + the admission valve
        // (worker-only, so N API replicas cannot multiply the admission rate).
        services.AddSingleton<RedisWaitingRoom>();
        services.AddSingleton<IWaitingRoom>(sp => sp.GetRequiredService<RedisWaitingRoom>());
        if (runsWorkers)
            services.AddHostedService<WaitingRoomAdmitter>();

        return services;
    }

    /// <summary>
    /// Local filesystem in dev; S3/MinIO for genuinely shared, multi-replica-safe blob storage
    /// (a ticket PDF written by one pod is readable by every other). The <see cref="IFileStorage"/>
    /// port is identical either way, so no caller changes when the provider flips.
    /// </summary>
    private static void AddFileStorage(IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration["FileStorage:Provider"] ?? "Local";
        if (!string.Equals(provider, "S3", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IFileStorage, LocalFileStorage>();
            return;
        }

        var options = configuration.GetSection(S3StorageOptions.SectionName).Get<S3StorageOptions>() ?? new();
        services.AddSingleton(options);
        services.AddSingleton<IAmazonS3>(_ =>
        {
            var config = new AmazonS3Config
            {
                ForcePathStyle = options.ForcePathStyle,   // MinIO addresses buckets by path
                AuthenticationRegion = options.Region,
                // SDK v4 defaults to adding a flexible (CRC32) checksum trailer, which MinIO and
                // other S3-compatible stores reject with an x-amz-content-sha256 mismatch. Only add
                // checksums when the operation requires them - real AWS S3 is unaffected.
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
            };
            if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
                config.ServiceURL = options.ServiceUrl;    // MinIO / non-AWS endpoint
            return new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config);
        });
        services.AddSingleton<IFileStorage, S3FileStorage>();
        // Ensure the bucket exists before the ticket issuer (or an API read) touches it.
        services.AddHostedService<S3BucketInitializer>();
    }
}
