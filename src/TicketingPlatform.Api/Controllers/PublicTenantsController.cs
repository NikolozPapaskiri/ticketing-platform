using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TicketingPlatform.Application.Contracts;
using TicketingPlatform.Application.Services;

namespace TicketingPlatform.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[AllowAnonymous]
[Route("api/v{version:apiVersion}/public/tenants")]
public sealed class PublicTenantsController : ControllerBase
{
    private readonly TenantService _tenants;

    public PublicTenantsController(TenantService tenants) => _tenants = tenants;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TenantResponse>>> List(CancellationToken ct) =>
        Ok(await _tenants.ListAsync(ct));
}
