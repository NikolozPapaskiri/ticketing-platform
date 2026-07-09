using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Infrastructure.Persistence;
using TicketingPlatform.Infrastructure.Persistence.Repositories;

namespace TicketingPlatform.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers Infrastructure services. The EF Core provider choice and the connection string
    /// live here, so the Api project does not reference Npgsql or know how persistence is wired.
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<TicketingDbContext>(options => options.UseNpgsql(connectionString));

        // Repositories: Application-defined ports, EF-backed implementations. Scoped, same as
        // the DbContext they wrap.
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<IEventRepository, EventRepository>();
        services.AddScoped<IHoldRepository, HoldRepository>();

        return services;
    }
}
