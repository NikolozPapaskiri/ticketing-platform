using TicketingPlatform.Application.Abstractions;
using TicketingPlatform.Application.Common;
using TicketingPlatform.Application.Contracts;
using TicketingPlatform.Domain;

namespace TicketingPlatform.Application.Services;

/// <summary>
/// Tenant use cases. Framework-free: no HTTP, no EF — it talks to the ITenantRepository port
/// and reports outcomes as Results. The slug-uniqueness pre-check lives here (it is a business
/// rule), but the authoritative guard remains the DB unique index (check-then-insert races).
/// </summary>
public sealed class TenantService
{
    private readonly ITenantRepository _tenants;
    public TenantService(ITenantRepository tenants) => _tenants = tenants;

    public async Task<IReadOnlyList<TenantResponse>> ListAsync(CancellationToken ct)
    {
        var tenants = await _tenants.ListAsync(ct);
        return tenants.Select(t => new TenantResponse(t.Id, t.Name, t.Slug)).ToList();
    }

    public async Task<Result<TenantResponse>> CreateAsync(CreateTenantRequest request, CancellationToken ct)
    {
        if (await _tenants.SlugExistsAsync(request.Slug, ct))
            return Result<TenantResponse>.Conflict($"A tenant with slug '{request.Slug}' already exists.");

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Slug = request.Slug,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _tenants.Add(tenant);
        await _tenants.SaveChangesAsync(ct);

        return Result<TenantResponse>.Success(new TenantResponse(tenant.Id, tenant.Name, tenant.Slug));
    }
}
