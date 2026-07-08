using FluentValidation;
using TicketingPlatform.Api.Common;
using TicketingPlatform.Api.Tenancy;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Application.Services;
using TicketingPlatform.Application.Validation;
using TicketingPlatform.Infrastructure;
using Asp.Versioning;

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

// Tenancy: TenantContext is scoped (one per request). It is exposed both as the
// concrete type (set by the middleware) and as the read-only ITenantContext (read by the DbContext).
builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

// Validators
builder.Services.AddValidatorsFromAssemblyContaining<CreateEventRequestValidator>();
builder.Services.AddSingleton(TimeProvider.System); // real clock in prod; tests pass

builder.Services.AddInfrastructure(builder.Configuration.GetConnectionString("Default")!);

// Application use-case services. Scoped: they hold a scoped repository.
builder.Services.AddScoped<TenantService>();
builder.Services.AddScoped<EventService>();

var app = builder.Build();

// Correlation id wraps everything, including the exception handler, so error logs carry it.
app.UseMiddleware<CorrelationIdMiddleware>();

// Exception handler comes before the endpoints it protects.
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi(); // serves /openapi/v1.json
}

// Resolve the tenant from the X-Tenant-Id header before controllers run.
app.UseMiddleware<TenantResolutionMiddleware>();

app.MapControllers();

app.Run();
