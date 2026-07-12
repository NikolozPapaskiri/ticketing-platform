using System.Net;
using System.Text;
using System.Threading.RateLimiting;
using Asp.Versioning;
using Microsoft.AspNetCore.DataProtection;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TicketingPlatform.Api.Auth;
using TicketingPlatform.Api.Common;
using TicketingPlatform.Api.Development;
using TicketingPlatform.Api.Features.SalesReport;
using TicketingPlatform.Api.Realtime;
using TicketingPlatform.Api.Tenancy;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Application.Common;
using TicketingPlatform.Application.Services;
using TicketingPlatform.Application.Validation;
using TicketingPlatform.Domain;
using TicketingPlatform.Infrastructure;
using TicketingPlatform.Infrastructure.Health;
using TicketingPlatform.Infrastructure.Messaging;
using TicketingPlatform.Infrastructure.Persistence;
using TicketingPlatform.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);
const string WebHubCorsPolicy = "web-hub";

builder.Services.AddControllers(options =>
{
    options.Filters.Add<FluentValidationFilter>();
});
builder.Services.AddOpenApi();

builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new ApiVersion(1, 0);
    options.AssumeDefaultVersionWhenUnspecified = true;   // no version specified => treat as v1.0
    options.ReportApiVersions = true;                     // adds 'api-supported-versions' response header
})
.AddMvc()                                                  // controller integration (Asp.Versioning.Mvc)
.AddApiExplorer(options =>
{
    options.GroupNameFormat = "'v'VVV";                    // formats group as v1, v2, ...
    options.SubstituteApiVersionInUrl = true;              // fills {version:apiVersion} in generated URLs
});

// RFC 7807 ProblemDetails + a global exception handler.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

var dataProtectionKeysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
{
    var keysPath = Path.IsPathRooted(dataProtectionKeysPath)
        ? dataProtectionKeysPath
        : Path.Combine(builder.Environment.ContentRootPath, dataProtectionKeysPath);

    Directory.CreateDirectory(keysPath);
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
        .SetApplicationName("ticketing-platform");
}

// --- Authentication: JWT bearer. The API validates issuer, audience, lifetime, and signature
// on EVERY request; a JWT is signed, not encrypted, so validation is the whole security story.
var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
    ?? throw new InvalidOperationException("Missing 'Jwt' configuration section.");

// Fail fast on a weak/missing/left-over-dev signing key rather than mint forgeable tokens.
SecurityOptionsValidation.ValidateJwt(jwt, builder.Environment.IsDevelopment());

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Keep the raw OIDC claim names (sub/role/tenant_id) instead of the legacy
        // SOAP-era ClaimTypes remapping - explicit beats magic.
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30), // default is 5 min - far too generous for 15-min tokens
            RoleClaimType = "role",
            NameClaimType = JwtRegisteredClaimNames.Sub
        };
    });

// --- Authorization: policy-based. Roles alone are too coarse; the OrganizerStaff policy
// requires the role AND a tenant claim (a staff token without a tenant is useless by design).
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AuthPolicies.OrganizerStaff, policy => policy
        .RequireRole(nameof(UserRole.OrganizerStaff))
        .RequireClaim(TenantResolutionMiddleware.TenantClaim));

    options.AddPolicy(AuthPolicies.PlatformAdmin, policy => policy
        .RequireRole(nameof(UserRole.PlatformAdmin)));
});

// Tenancy: TenantContext is scoped (one per request). It is exposed both as the
// concrete type (set by the middleware) and as the read-only ITenantContext (read by the DbContext).
builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

// Validators
builder.Services.AddValidatorsFromAssemblyContaining<CreateEventRequestValidator>();
builder.Services.AddSingleton(TimeProvider.System); // real clock in prod; tests pass a fake

builder.Services.AddInfrastructure(builder.Configuration);

// Application use-case services. Scoped: they hold scoped repositories.
builder.Services.AddScoped<TenantService>();
builder.Services.AddScoped<EventService>();
builder.Services.AddScoped<HoldService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<TicketService>();
builder.Services.AddScoped<WaitingRoomService>();

// Hold TTL / expiry-scan settings (plain singleton so Application stays free of IOptions).
builder.Services.AddSingleton(
    builder.Configuration.GetSection(TicketingPlatform.Application.Common.HoldOptions.SectionName)
        .Get<TicketingPlatform.Application.Common.HoldOptions>() ?? new());

// Waiting-room valve settings (admission batch size / interval / admission TTL).
builder.Services.AddSingleton(
    builder.Configuration.GetSection(TicketingPlatform.Application.Common.WaitingRoomOptions.SectionName)
        .Get<TicketingPlatform.Application.Common.WaitingRoomOptions>() ?? new());

// Refresh-session tuning (rotation grace window). Plain singleton, same as the options above.
builder.Services.AddSingleton(
    builder.Configuration.GetSection(TicketingPlatform.Application.Common.AuthSessionOptions.SectionName)
        .Get<TicketingPlatform.Application.Common.AuthSessionOptions>() ?? new());

// SignalR with the Redis backplane: group membership and broadcasts flow through Redis, so a
// message published from pod B reaches a client connected to pod A. Without the backplane,
// live availability silently breaks the moment there is a second replica.
builder.Services.AddSignalR()
    .AddStackExchangeRedis(builder.Configuration.GetConnectionString("Redis")!);
builder.Services.AddSingleton<IAvailabilityBroadcaster, SignalRAvailabilityBroadcaster>();
builder.Services.AddSingleton<IQueueBroadcaster, SignalRQueueBroadcaster>();

builder.Services.AddCors(options =>
{
    options.AddPolicy(WebHubCorsPolicy, policy =>
    {
        var webOrigin = builder.Configuration["Cors:WebOrigin"] ?? "http://localhost:3000";
        policy.WithOrigins(webOrigin)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

// --- Health checks. Liveness ("is the process alive") deliberately checks NOTHING external:
// a pod that is alive but missing a dependency must fail READINESS (stop routing traffic to
// it), not liveness (restarting it won't bring Postgres back). Conflating the two causes the
// classic restart-loop-during-an-outage incident.
builder.Services.AddSingleton<RedisHealthCheck>();
builder.Services.AddSingleton<RabbitMqHealthCheck>();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<TicketingDbContext>("postgres", tags: ["ready"])
    .AddCheck<RedisHealthCheck>("redis", tags: ["ready"])
    .AddCheck<RabbitMqHealthCheck>("rabbitmq", tags: ["ready"]);

// --- Trusted reverse proxy: when configured, the real client IP comes from X-Forwarded-For, but
// only when the immediate peer is a trusted proxy (else the header is spoofable and would let an
// attacker forge or poison rate-limit partitions). Off by default => the socket peer IP is used.
var reverseProxy = builder.Configuration.GetSection(ReverseProxyOptions.SectionName).Get<ReverseProxyOptions>() ?? new();
if (reverseProxy.Enabled)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = reverseProxy.ForwardLimit;
        // Start from an EMPTY trust list (the framework defaults trust loopback) and add only the
        // proxies we were explicitly told about.
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var proxy in reverseProxy.KnownProxies)
            options.KnownProxies.Add(IPAddress.Parse(proxy));
        foreach (var network in reverseProxy.KnownNetworks)
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
    });
}

// --- Rate limiting on the auth endpoints: cut brute force off BEFORE PBKDF2 burns CPU per
// guess. Fixed window per client IP; limit configurable (tests raise it, prod keeps it tight).
var authRequestsPerMinute = builder.Configuration.GetValue("RateLimiting:AuthRequestsPerMinute", 20);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = authRequestsPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0 // reject immediately; queuing auth attempts helps nobody
        }));
});

// --- OpenTelemetry: traces (HTTP in, HTTP out, Npgsql SQL, and the custom messaging source
// that carries traces across the RabbitMQ hop) + metrics. Exported over OTLP when
// Otlp:Endpoint is configured (Jaeger/Grafana etc.); otherwise instrumentation is on but quiet.
var otlpEndpoint = builder.Configuration["Otlp:Endpoint"];
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("ticketing-api"))
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation()
               .AddHttpClientInstrumentation()
               .AddSource("Npgsql")                            // SQL spans from the driver
               .AddSource(MessagingDiagnostics.SourceName);    // outbox publish/consume spans
        if (otlpEndpoint is not null)
            tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
    })
    .WithMetrics(metrics =>
    {
        metrics.AddAspNetCoreInstrumentation()
               .AddHttpClientInstrumentation()
               .AddRuntimeInstrumentation()
               .AddMeter(TicketingMetrics.MeterName);
        if (otlpEndpoint is not null)
            metrics.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
    });

// --- Graceful shutdown: on SIGTERM (K8s pod termination) the host stops accepting requests,
// then gives in-flight requests and the background services' stopping tokens this long to
// drain before the process dies. A mid-saga order gets to finish.
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

var app = builder.Build();

// Correlation id wraps everything, including the exception handler, so error logs carry it.
app.UseMiddleware<CorrelationIdMiddleware>();

// Exception handler comes before the endpoints it protects.
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi(); // serves /openapi/v1.json

    // DEV-ONLY mock payment provider so the saga runs locally with zero external deps.
    // The resilient PaymentProviderClient points here via PaymentProvider:BaseUrl; tests
    // exercise the failure paths against WireMock instead. It remembers charged keys so the
    // reconciliation lookup (GET charges/{key}) can answer truthfully - and so charging the
    // same key twice returns the same id, exactly like a real idempotent provider.
    var devCharges = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
    app.MapPost("/dev-payment/charges", (HttpRequest req) =>
    {
        var key = req.Headers["Idempotency-Key"].FirstOrDefault();
        var chargeId = key is null
            ? $"ch_{Guid.NewGuid():N}"
            : devCharges.GetOrAdd(key, _ => $"ch_{Guid.NewGuid():N}");
        return Results.Ok(new { chargeId });
    });
    app.MapGet("/dev-payment/charges/{key}", (string key) =>
        devCharges.TryGetValue(key, out var chargeId)
            ? Results.Ok(new { status = "charged", chargeId })
            : Results.NotFound());
    app.MapPost("/dev-payment/refunds", () => Results.Ok(new { refundId = $"rf_{Guid.NewGuid():N}" }));

    // Development convenience: migrate + seed the first PlatformAdmin so the closed
    // provisioning chain (only admins create staff) can start. Production migrates
    // deliberately (CI/CD step), never implicitly at boot.
    using (var scope = app.Services.CreateScope())
    {
        await scope.ServiceProvider.GetRequiredService<TicketingDbContext>().Database.MigrateAsync();
    }
    await DevDataSeeder.SeedAsync(app.Services);
}

// Rewrite RemoteIpAddress from the trusted proxy's X-Forwarded-For BEFORE the rate limiter reads
// it, so brute-force windows partition on the real client, not the shared ingress address.
if (reverseProxy.Enabled)
    app.UseForwardedHeaders();

// Rate limiting sits before authentication: a brute-forcer gets 429'd without ever reaching
// the password hasher.
app.UseRateLimiter();
app.UseCors();

// Pipeline order matters: authenticate (who are you) -> resolve tenant FROM the principal's
// claim -> authorize (are you allowed) -> endpoint.
app.UseAuthentication();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthorization();

// Probe endpoints (anonymous by design - Kubernetes is not going to log in).
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

app.MapHub<AvailabilityHub>("/hubs/availability")
    .RequireCors(WebHubCorsPolicy); // live availability push (anonymous by design)

// The vertical-slice set piece (see the file's header for the architecture argument).
app.MapEventSalesReport();

app.MapControllers();

app.Run();

// Minimal hosting generates an internal Program class; WebApplicationFactory<Program> in the
// integration tests needs it visible. This partial declaration only changes accessibility.
public partial class Program { }
