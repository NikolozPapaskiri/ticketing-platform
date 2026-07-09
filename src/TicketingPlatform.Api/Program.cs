using System.Text;
using Asp.Versioning;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TicketingPlatform.Api.Auth;
using TicketingPlatform.Api.Common;
using TicketingPlatform.Api.Development;
using TicketingPlatform.Api.Tenancy;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Application.Services;
using TicketingPlatform.Application.Validation;
using TicketingPlatform.Domain;
using TicketingPlatform.Infrastructure;
using TicketingPlatform.Infrastructure.Persistence;
using TicketingPlatform.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);

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

    // Development convenience: migrate + seed the first PlatformAdmin so the closed
    // provisioning chain (only admins create staff) can start. Production migrates
    // deliberately (CI/CD step), never implicitly at boot.
    using (var scope = app.Services.CreateScope())
    {
        await scope.ServiceProvider.GetRequiredService<TicketingDbContext>().Database.MigrateAsync();
    }
    await DevDataSeeder.SeedAsync(app.Services);
}

// Pipeline order matters: authenticate (who are you) -> resolve tenant FROM the principal's
// claim -> authorize (are you allowed) -> endpoint.
app.UseAuthentication();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthorization();

app.MapControllers();

app.Run();

// Minimal hosting generates an internal Program class; WebApplicationFactory<Program> in the
// integration tests needs it visible. This partial declaration only changes accessibility.
public partial class Program { }
