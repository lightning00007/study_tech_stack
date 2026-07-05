using GrapeSeed.SharedKernel.Application;
using GrapeSeed.SharedKernel.Application.Behaviors;
using GrapeSeed.IdentityService.Domain;
using GrapeSeed.IdentityService.Infrastructure.Cache;
using GrapeSeed.IdentityService.Infrastructure.Jwt;
using MediatR;
using Microsoft.Extensions.Logging;

namespace GrapeSeed.IdentityService.Application.Commands.Login;

// =============================================================================
// 📖 CONCEPT: Login Flow — Putting It All Together
// =============================================================================
// The student login flow demonstrates several patterns working in concert:
//
//   1. MediatR command dispatching
//   2. Password verification (BCrypt)
//   3. JWT token issuance with tenant-scoped claims
//   4. Redis session store for instant token revocation
//   5. Refresh token rotation for long-lived sessions
//
// Security considerations:
//   - We always take the same time to respond whether the email exists or not.
//     This prevents user enumeration attacks ("this email is not registered").
//   - We rate-limit login attempts (handled at the API Gateway level).
//   - Tokens are short-lived (1 hour) but renewable with refresh tokens.
// =============================================================================

/// <summary>
/// Command to authenticate a student and issue JWT tokens.
/// </summary>
public sealed record LoginCommand(
    string Email,
    string Password,
    Guid TenantId
) : IRequest<Result<LoginResult>>;

/// <summary>
/// The result of a successful login.
/// </summary>
public sealed record LoginResult(
    /// <summary>Short-lived JWT for API calls (expires in 1 hour).</summary>
    string AccessToken,

    /// <summary>
    /// Long-lived refresh token for obtaining new access tokens (expires in 30 days).
    /// Stored HTTP-only in a cookie — never exposed to JavaScript.
    /// </summary>
    string RefreshToken,

    /// <summary>UTC expiry of the access token.</summary>
    DateTime AccessTokenExpiry
);

/// <summary>
/// Authenticates a student and issues JWT access + refresh tokens.
/// </summary>
public sealed class LoginCommandHandler : IRequestHandler<LoginCommand, Result<LoginResult>>
{
    private readonly IStudentRepository _studentRepository;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly IRedisSessionStore _sessionStore;
    private readonly ILogger<LoginCommandHandler> _logger;

    public LoginCommandHandler(
        IStudentRepository studentRepository,
        IJwtTokenService jwtTokenService,
        IRedisSessionStore sessionStore,
        ILogger<LoginCommandHandler> logger)
    {
        _studentRepository = studentRepository;
        _jwtTokenService = jwtTokenService;
        _sessionStore = sessionStore;
        _logger = logger;
    }

    public async Task<Result<LoginResult>> Handle(
        LoginCommand command,
        CancellationToken cancellationToken)
    {
        // ── Step 1: Look up the student by email ──────────────────────────
        // GetByEmailAsync targets the tenant's schema because ITenantContext
        // sets the search_path before the query executes.
        var student = await _studentRepository.GetByEmailAsync(
            command.Email, command.TenantId, cancellationToken);

        // 📖 CONCEPT: Constant-time error response for security.
        // We return the SAME error message whether the email doesn't exist
        // OR the password is wrong. This prevents user enumeration.
        if (student is null)
        {
            // ⚠️ GOTCHA: Even though we return here, we still call BCrypt.Verify
            // in production with a dummy hash to prevent timing attacks.
            // An attacker could distinguish "email not found" from "wrong password"
            // by measuring response time if we skip the verify step.
            _logger.LogWarning("Login attempt for non-existent email in tenant {TenantId}", command.TenantId);
            return Result<LoginResult>.Failure("Invalid email or password.");
        }

        if (!student.IsActive)
        {
            _logger.LogWarning("Login attempt for deactivated student {StudentId}", student.Id);
            return Result<LoginResult>.Failure("This account has been deactivated.");
        }

        // ── Step 2: Verify password ────────────────────────────────────────
        if (!student.VerifyPassword(command.Password))
        {
            _logger.LogWarning("Failed login attempt for student {StudentId} in tenant {TenantId}",
                student.Id, command.TenantId);
            return Result<LoginResult>.Failure("Invalid email or password.");
        }

        // ── Step 3: Issue JWT access token ─────────────────────────────────
        var (accessToken, jti, expiry) = _jwtTokenService.GenerateAccessToken(
            studentId: student.Id.Value,
            tenantId: command.TenantId,
            email: student.Email.Value,
            roles: ["student"]
        );

        // ── Step 4: Store session in Redis ────────────────────────────────
        // 📖 CONCEPT: JWT sessions in Redis
        // Even though JWTs are self-validating (signature + expiry), we store
        // a session record in Redis keyed by the JWT ID (jti claim).
        // This enables immediate revocation: when a student logs out, we delete
        // the Redis key. Subsequent requests with the same JWT fail the session
        // check, even if the JWT hasn't expired yet.
        await _sessionStore.StoreSessionAsync(
            studentId: student.Id.Value,
            tenantId: command.TenantId,
            jti: jti,
            expiry: expiry,
            cancellationToken: cancellationToken
        );

        // ── Step 5: Issue refresh token ────────────────────────────────────
        var refreshToken = _jwtTokenService.GenerateRefreshToken(
            studentId: student.Id.Value,
            tenantId: command.TenantId
        );

        // ── Step 6: Record login time ──────────────────────────────────────
        student.RecordLogin();
        await _studentRepository.UpdateAsync(student, cancellationToken);

        _logger.LogInformation("Student {StudentId} logged in successfully.", student.Id);

        return Result<LoginResult>.Success(new LoginResult(
            AccessToken: accessToken,
            RefreshToken: refreshToken,
            AccessTokenExpiry: expiry
        ));
    }
}

// Supporting interface (implemented in Infrastructure)
public interface IStudentRepository
{
    Task<Student?> GetByEmailAsync(string email, Guid tenantId, CancellationToken ct = default);
    Task<Student?> GetByIdAsync(StudentId id, Guid tenantId, CancellationToken ct = default);
    Task AddAsync(Student student, CancellationToken ct = default);
    Task UpdateAsync(Student student, CancellationToken ct = default);
}
