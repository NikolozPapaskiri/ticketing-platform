using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TicketingPlatform.Api.Auth;
using TicketingPlatform.Application.Common;
using TicketingPlatform.Application.Contracts;
using TicketingPlatform.Application.Services;

namespace TicketingPlatform.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Authorize(Policy = AuthPolicies.OrganizerStaff)]
[Route("api/v{version:apiVersion}/tickets")]
public sealed class TicketsController : ControllerBase
{
    private readonly TicketService _tickets;

    public TicketsController(TicketService tickets) => _tickets = tickets;

    [HttpPost("validate")]
    public async Task<ActionResult<TicketValidationResponse>> Validate(
        ValidateTicketRequest request, CancellationToken ct)
    {
        var result = await _tickets.ValidateAsync(request.Code, ct);
        return result.Error switch
        {
            ResultError.None => Ok(result.Value),
            ResultError.NotFound => NotFound(),
            _ => Problem(statusCode: StatusCodes.Status409Conflict, title: "Ticket cannot be validated", detail: result.Message)
        };
    }
}
