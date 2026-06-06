using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TicketingPlatform.Api.Contracts;
using TicketingPlatform.Api.Data;
using TicketingPlatform.Api.Domain;

namespace TicketingPlatform.Api.Controllers;

/// <summary>
/// Platform-admin operations on tenants. Tenant is not tenant-scoped, so these endpoints work
/// without an X-Tenant-Id header. In Phase 3 they are restricted to the platform-admin role.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/tenants")]
public class TenantsController : ControllerBase
{
    private readonly TicketingDbContext _db;

    public TenantsController(TicketingDbContext db) => _db = db;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TenantResponse>>> List(CancellationToken ct)
    {
        var tenants = await _db.Tenants
            .AsNoTracking()
            .OrderBy(t => t.Name)
            .Select(t => new TenantResponse(t.Id, t.Name, t.Slug))
            .ToListAsync(ct);

        return Ok(tenants);
    }

    [HttpPost]
    public async Task<ActionResult<TenantResponse>> Create(CreateTenantRequest request, CancellationToken ct)
    {
        var slugTaken = await _db.Tenants.AnyAsync(t => t.Slug == request.Slug, ct);
        if (slugTaken)
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Slug already in use",
                detail: $"A tenant with slug '{request.Slug}' already exists.");
        }

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Slug = request.Slug,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync(ct);

        var response = new TenantResponse(tenant.Id, tenant.Name, tenant.Slug);
        return CreatedAtAction(nameof(List), null, response);
    }
}
