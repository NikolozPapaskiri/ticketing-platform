using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Application.Contracts;
using TicketingPlatform.Application.Services;
using TicketingPlatform.Domain;

namespace TicketingPlatform.Api.Development;

/// <summary>
/// Development-only bootstrap. Two parts:
/// 1. The first PlatformAdmin - always seeded, because staff/admin accounts can only be
///    provisioned BY a PlatformAdmin (the chicken-and-egg of closed registration).
/// 2. Demo marketplace data (a tenant, staff, and published categorized events) - gated by
///    "Seed:DemoData" so a fresh `docker compose up` or `dotnet run` demos instantly, while
///    the integration-test host (which does not set the flag) stays clean.
/// Credentials here are dev-only and documented; production provisions out of band.
/// </summary>
public static class DevDataSeeder
{
    public const string AdminEmail = "admin@platform.local";
    public const string AdminPassword = "Admin123$"; // DEV ONLY - never a real secret

    public const string DemoTenantSlug = "capital-events";
    public const string DemoStaffEmail = "demo-staff@capital.local";
    public const string DemoStaffPassword = "Staff123$"; // DEV ONLY
    public const string DemoCustomerEmail = "demo-buyer@example.com";
    public const string DemoCustomerPassword = "Buyer123$"; // DEV ONLY

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        await SeedAdminAsync(scope.ServiceProvider, ct);

        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        if (configuration.GetValue("Seed:DemoData", false))
            await SeedDemoMarketplaceAsync(scope.ServiceProvider, ct);
    }

    private static async Task SeedAdminAsync(IServiceProvider services, CancellationToken ct)
    {
        var users = services.GetRequiredService<IUserRepository>();
        var hasher = services.GetRequiredService<IPasswordHasherService>();

        var normalized = AdminEmail.ToUpperInvariant();
        if (await users.EmailExistsAsync(normalized, ct))
            return; // idempotent: reruns and restarts are no-ops

        users.Add(NewUser(AdminEmail, AdminPassword, UserRole.PlatformAdmin, null, hasher));
        await users.SaveChangesAsync(ct);
    }

    private static async Task SeedDemoMarketplaceAsync(IServiceProvider services, CancellationToken ct)
    {
        var tenants = services.GetRequiredService<TenantService>();
        var events = services.GetRequiredService<EventService>();
        var users = services.GetRequiredService<IUserRepository>();
        var hasher = services.GetRequiredService<IPasswordHasherService>();

        // Idempotency anchor: the demo tenant's slug.
        var created = await tenants.CreateAsync(new CreateTenantRequest("Capital Events", DemoTenantSlug), ct);
        if (!created.IsSuccess)
            return; // slug exists -> demo data already seeded

        var tenantId = created.Value!.Id;

        if (!await users.EmailExistsAsync(DemoStaffEmail.ToUpperInvariant(), ct))
            users.Add(NewUser(DemoStaffEmail, DemoStaffPassword, UserRole.OrganizerStaff, tenantId, hasher));
        if (!await users.EmailExistsAsync(DemoCustomerEmail.ToUpperInvariant(), ct))
            users.Add(NewUser(DemoCustomerEmail, DemoCustomerPassword, UserRole.Customer, null, hasher));
        await users.SaveChangesAsync(ct);

        // Categorized events across the icon nav; gradients carry the imageless cards by design.
        var demo = new (string Name, string Category, string Venue, int Days, decimal Price, int Qty)[]
        {
            ("Midnight Symphony Orchestra", "Concert", "Philharmonic Hall", 6, 45m, 300),
            ("Hamlet Reimagined", "Theatre", "Royal District Theatre", 9, 30m, 180),
            ("Derby Finals 2026", "Sport", "National Arena", 13, 25m, 1000),
            ("Open Air Film Nights", "Cinema", "Riverside Park", 3, 12m, 250),
            ("Summer Beats Festival", "Festival", "Lake Shore Grounds", 21, 79m, 1500),
            ("Laugh Lab Stand-up", "StandUp", "Basement Club", 2, 18m, 80),
            ("La Traviata", "Opera", "State Opera House", 16, 60m, 220),
            ("Little Explorers Day", "Kids", "City Science Park", 5, 9m, 400)
        };

        foreach (var (name, category, venue, days, price, qty) in demo)
        {
            var ev = await events.CreateAsync(tenantId, new CreateEventRequest(
                name, $"Live at {venue}. A demo event on the marketplace.", venue,
                DateTimeOffset.UtcNow.AddDays(days), category), ct);
            await events.AddTicketTypeAsync(tenantId, ev.Id,
                new CreateTicketTypeRequest("General Admission", price, "USD", qty), ct);
            await events.AddTicketTypeAsync(tenantId, ev.Id,
                new CreateTicketTypeRequest("VIP", price * 2.5m, "USD", Math.Max(10, qty / 10)), ct);
            await events.TransitionAsync(tenantId, ev.Id, EventStatus.OnSale, ct);
        }
    }

    private static User NewUser(string email, string password, UserRole role, Guid? tenantId, IPasswordHasherService hasher)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            PasswordHash = string.Empty,
            Role = role,
            TenantId = tenantId,
            CreatedAt = DateTimeOffset.UtcNow
        };
        user.PasswordHash = hasher.Hash(user, password);
        return user;
    }
}
