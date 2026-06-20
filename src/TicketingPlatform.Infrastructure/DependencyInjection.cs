using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketingPlatform.Infrastructure.Persistence;

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
        return services;
    }
}
