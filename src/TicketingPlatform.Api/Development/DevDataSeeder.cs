using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Domain;

namespace TicketingPlatform.Api.Development;

/// <summary>
/// Development-only bootstrap: someone has to be the first PlatformAdmin, because staff/admin
/// accounts can only be provisioned BY a PlatformAdmin (the chicken-and-egg of closed
/// registration). The credentials are dev-only and documented; production provisions its first
/// admin out of band (ops script / secret store), never with a committed password.
/// </summary>
public static class DevDataSeeder
{
    public const string AdminEmail = "admin@platform.local";
    public const string AdminPassword = "Admin123$"; // DEV ONLY - never a real secret

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasherService>();

        var normalized = AdminEmail.ToUpperInvariant();
        if (await users.EmailExistsAsync(normalized, ct))
            return; // idempotent: reruns and restarts are no-ops

        var admin = new User
        {
            Id = Guid.NewGuid(),
            Email = AdminEmail,
            NormalizedEmail = normalized,
            PasswordHash = string.Empty,
            Role = UserRole.PlatformAdmin,
            TenantId = null,
            CreatedAt = DateTimeOffset.UtcNow
        };
        admin.PasswordHash = hasher.Hash(admin, AdminPassword);

        users.Add(admin);
        await users.SaveChangesAsync(ct);
    }
}
