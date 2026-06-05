using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TicketingPlatform.Api.Common;
using TicketingPlatform.Api.Data;
using TicketingPlatform.Api.Tenancy;
using TicketingPlatform.Api.Validation;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();

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

builder.Services.AddDbContext<TicketingDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

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
