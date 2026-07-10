using System.Text;
using System.Threading.RateLimiting;
using Asp.Versioning;
using Microsoft.AspNetCore.DataProtection;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
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

// Hold TTL / expiry-scan settings (plain singleton so Application stays free of IOptions).
builder.Services.AddSingleton(
    builder.Configuration.GetSection(TicketingPlatform.Application.Common.HoldOptions.SectionName)
        .Get<TicketingPlatform.Application.Common.HoldOptions>() ?? new());

// SignalR with the Redis backplane: group membership and broadcasts flow through Redis, so a
// message published from pod B reaches a client connected to pod A. Without the backplane,
// live availability silently breaks the moment there is a second replica.
builder.Services.AddSignalR()
    .AddStackExchangeRedis(builder.Configuration.GetConnectionString("Redis")!);
builder.Services.AddSingleton<IAvailabilityBroadcaster, SignalRAvailabilityBroadcaster>();

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
    // exercise the failure paths against WireMock instead.
    app.MapPost("/dev-payment/charges", () => Results.Ok(new { chargeId = $"ch_{Guid.NewGuid():N}" }));
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
