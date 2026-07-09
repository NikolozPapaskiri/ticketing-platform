using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Infrastructure.Persistence;
using TicketingPlatform.Infrastructure.Persistence.Repositories;
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

        // Security: PBKDF2 hashing + JWT creation. Singletons - both are stateless.
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.AddSingleton<IPasswordHasherService, PasswordHasherService>();
        services.AddSingleton<IJwtTokenGenerator, JwtTokenGenerator>();

        return services;
    }
}
