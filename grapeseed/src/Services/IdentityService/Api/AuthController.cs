using GrapeSeed.IdentityService.Application.Commands.Login;
using GrapeSeed.SharedKernel.Application;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace GrapeSeed.IdentityService.Api;

/// <summary>HTTP API for student authentication — login, refresh, and logout.</summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly IMediator _mediator;

    public AuthController(IMediator mediator) => _mediator = mediator;

    // =========================================================================
    // POST /api/auth/login
    // Public endpoint — no JWT required.
    // =========================================================================

    /// <summary>
    /// Authenticates a student with email and password.
    /// Returns a short-lived JWT access token and a long-lived refresh token.
    /// </summary>
    [HttpPost("login")]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        var command = new LoginCommand(
            Email: request.Email,
            Password: request.Password,
            TenantId: request.TenantId
        );

        var result = await _mediator.Send(command, cancellationToken);

        if (result.IsFailure)
        {
            // 📖 CONCEPT: 401 Unauthorized (not 404) for invalid credentials.
            // Never tell the client WHY login failed (which field is wrong).
            // A generic "Invalid email or password" prevents user enumeration.
            return Unauthorized(new ProblemDetails
            {
                Title = "Authentication Failed",
                Detail = result.Error,
                Status = StatusCodes.Status401Unauthorized
            });
        }

        // 📖 CONCEPT: Refresh token in HTTP-Only cookie.
        // The refresh token is never returned in the response body.
        // It's stored in a cookie with these flags:
        //   - HttpOnly: JavaScript cannot read it (XSS protection).
        //   - Secure: Only sent over HTTPS.
        //   - SameSite=Strict: Not sent with cross-site requests (CSRF protection).
        Response.Cookies.Append("refresh_token", result.Value!.RefreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Expires = DateTimeOffset.UtcNow.AddDays(30)
        });

        return Ok(new LoginResponse(
            AccessToken: result.Value.AccessToken,
            ExpiresAt: result.Value.AccessTokenExpiry
        ));
    }

    // =========================================================================
    // POST /api/auth/logout
    // Requires valid JWT (via API Gateway validation)
    // =========================================================================

    /// <summary>
    /// Logs out the student by revoking the current session in Redis.
    /// The JWT becomes immediately invalid even if it hasn't expired.
    /// </summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        // 📖 CONCEPT: The jti (JWT ID) and student ID come from the JWT claims.
        // The API Gateway validated the JWT and the TenantMiddleware extracted claims.
        // We don't need to validate the JWT again here.
        var jti = User.FindFirst("jti")?.Value;
        var studentIdClaim = User.FindFirst("sub")?.Value;

        if (string.IsNullOrEmpty(jti) || !Guid.TryParse(studentIdClaim, out var studentId))
            return BadRequest();

        if (!Guid.TryParse(Request.Headers["X-Tenant-Id"], out var tenantId))
            return BadRequest();

        // Delete the Redis session — the JWT is now invalid
        var logoutCommand = new LogoutCommand(studentId, tenantId, jti);
        await _mediator.Send(logoutCommand, cancellationToken);

        // Clear the refresh token cookie
        Response.Cookies.Delete("refresh_token");

        return NoContent();
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

/// <summary>Login request body.</summary>
public sealed record LoginRequest(
    string Email,
    string Password,
    Guid TenantId
);

/// <summary>
/// Login response. Note: refresh token is in a cookie, NOT in this response body.
/// </summary>
public sealed record LoginResponse(
    string AccessToken,
    DateTime ExpiresAt
);

// Placeholder command for logout (implementation follows the same MediatR pattern)
public sealed record LogoutCommand(Guid StudentId, Guid TenantId, string Jti)
    : MediatR.IRequest<Result>;
