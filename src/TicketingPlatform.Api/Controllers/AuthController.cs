using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TicketingPlatform.Application.Common;
using TicketingPlatform.Application.Contracts;
using TicketingPlatform.Application.Services;
using TicketingPlatform.Domain;

namespace TicketingPlatform.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[EnableRateLimiting("auth")] // brute-force guard: fixed window per client IP
[Route("api/v{version:apiVersion}/auth")]
public class AuthController : ControllerBase
{
    private readonly AuthService _auth;
    public AuthController(AuthService auth) => _auth = auth;

    /// <summary>Self-service signup. Always creates a Customer - the role is never client-supplied.</summary>
    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<ActionResult<UserResponse>> Register(RegisterRequest request, CancellationToken ct)
    {
        var result = await _auth.RegisterCustomerAsync(request, ct);
        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : Problem(statusCode: StatusCodes.Status409Conflict, title: "Email already registered", detail: result.Message);
    }

    /// <summary>Provisioning staff/admin accounts is a platform-admin operation.</summary>
    [HttpPost("register-staff")]
    [Authorize(Roles = nameof(UserRole.PlatformAdmin))]
    public async Task<ActionResult<UserResponse>> RegisterStaff(RegisterStaffRequest request, CancellationToken ct)
    {
        var result = await _auth.RegisterStaffAsync(request, ct);
        return result.Error switch
        {
            ResultError.None => StatusCode(StatusCodes.Status201Created, result.Value),
            ResultError.NotFound => Problem(statusCode: StatusCodes.Status404NotFound, title: "Tenant not found", detail: result.Message),
            _ => Problem(statusCode: StatusCodes.Status409Conflict, title: "Email already registered", detail: result.Message)
        };
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var result = await _auth.LoginAsync(request, ct);
        return result.IsSuccess
            ? Ok(result.Value)
            : Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication failed", detail: result.Message);
    }

    /// <summary>Rotates the refresh token; a replayed (already-rotated) token revokes the whole family.</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResponse>> Refresh(RefreshRequest request, CancellationToken ct)
    {
        var result = await _auth.RefreshAsync(request, ct);
        return result.IsSuccess
            ? Ok(result.Value)
            : Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication failed", detail: result.Message);
    }
}
