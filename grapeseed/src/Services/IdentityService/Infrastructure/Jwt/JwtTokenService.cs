using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace GrapeSeed.IdentityService.Infrastructure.Jwt;

// =============================================================================
// 📖 CONCEPT: JSON Web Tokens (JWT)
// =============================================================================
// A JWT is a self-contained token that encodes:
//   1. HEADER: the signing algorithm (e.g., HS256, RS256)
//   2. PAYLOAD: claims — assertions about the user (sub, email, tenantId, roles)
//   3. SIGNATURE: HMAC or RSA signature proving the token was issued by us
//
// Structure: base64(header) + "." + base64(payload) + "." + base64(signature)
//
// Why JWTs?
//   - Stateless: any service can validate a JWT by checking the signature.
//     No database lookup needed for validation.
//   - Self-describing: the token carries all the information the service needs.
//   - Multi-tenant: we include tenantId and schema name in the claims.
//
// GrapeSeed's JWT claims:
//   sub (subject)    — the student's UUID
//   email            — the student's email
//   tenant_id        — the tenant UUID
//   tenant_schema    — the PostgreSQL schema name (e.g., "tenant_schoola")
//   roles            — ["student"] or ["admin"]
//   jti              — unique token ID (used for Redis session lookup)
//   iat              — issued-at timestamp
//   exp              — expiry timestamp (1 hour from issuance)
//
// ⚠️ GOTCHA: JWTs are NOT encrypted — they are only SIGNED.
// Anyone can base64-decode the payload and read the claims.
// NEVER put sensitive data (passwords, card numbers) in a JWT.
// =============================================================================

/// <summary>Contract for JWT token generation and validation.</summary>
public interface IJwtTokenService
{
    /// <summary>Generates a short-lived access token.</summary>
    (string Token, string Jti, DateTime Expiry) GenerateAccessToken(
        Guid studentId, Guid tenantId, string email, string[] roles);

    /// <summary>Generates an opaque refresh token.</summary>
    string GenerateRefreshToken(Guid studentId, Guid tenantId);

    /// <summary>Validates a JWT and returns its principal, or null if invalid.</summary>
    ClaimsPrincipal? ValidateToken(string token);
}

/// <summary>
/// Implementation of IJwtTokenService using System.IdentityModel.Tokens.Jwt.
/// </summary>
public sealed class JwtTokenService : IJwtTokenService
{
    private readonly string _secretKey;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly TimeSpan _accessTokenLifetime = TimeSpan.FromHours(1);

    public JwtTokenService(IConfiguration configuration)
    {
        // In production, use RS256 (asymmetric) and load the private key from
        // AWS Secrets Manager. HS256 (symmetric) is simpler but requires sharing
        // the secret with every service that validates tokens.
        _secretKey = configuration["Jwt:SecretKey"]
            ?? throw new InvalidOperationException("Jwt:SecretKey is required.");
        _issuer = configuration["Jwt:Issuer"] ?? "grapeseed-identity";
        _audience = configuration["Jwt:Audience"] ?? "grapeseed-api";
    }

    public (string Token, string Jti, DateTime Expiry) GenerateAccessToken(
        Guid studentId, Guid tenantId, string email, string[] roles)
    {
        var jti = Guid.NewGuid().ToString(); // Unique token ID for session lookup
        var expiry = DateTime.UtcNow.Add(_accessTokenLifetime);

        // 📖 CONCEPT: Claims are key-value pairs inside the JWT payload.
        // Standard claims use well-known names (sub, email, jti).
        // Custom claims (tenant_id, tenant_schema) extend the standard.
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, studentId.ToString()),
            new(JwtRegisteredClaimNames.Email, email),
            new(JwtRegisteredClaimNames.Jti, jti),         // Unique token ID
            new(JwtRegisteredClaimNames.Iat,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                ClaimValueTypes.Integer64),
            new("tenant_id", tenantId.ToString()),
            new("tenant_schema", $"tenant_{tenantId:N}"),  // PostgreSQL schema name
        };

        // Add each role as a separate claim
        foreach (var role in roles)
            claims.Add(new Claim(ClaimTypes.Role, role));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            expires: expiry,
            signingCredentials: credentials
        );

        var tokenString = new JwtSecurityTokenHandler().WriteToken(token);
        return (tokenString, jti, expiry);
    }

    public string GenerateRefreshToken(Guid studentId, Guid tenantId)
    {
        // 📖 CONCEPT: Refresh tokens are NOT JWTs. They are random, opaque strings.
        // The server stores a hashed version in the database (refresh_tokens table).
        // When the client presents a refresh token, the server:
        //   1. Looks up the hash in the database.
        //   2. Verifies the token hasn't expired or been revoked.
        //   3. Issues a new access token + new refresh token (rotation).
        //   4. Revokes the old refresh token (prevents reuse after rotation).
        //
        // Using cryptographically secure random bytes prevents brute-force guessing.
        var randomBytes = RandomNumberGenerator.GetBytes(64);
        return Convert.ToBase64String(randomBytes);
    }

    public ClaimsPrincipal? ValidateToken(string token)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secretKey));

        try
        {
            var principal = new JwtSecurityTokenHandler().ValidateToken(token,
                new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = key,
                    ValidateIssuer = true,
                    ValidIssuer = _issuer,
                    ValidateAudience = true,
                    ValidAudience = _audience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(1) // 1-minute tolerance for clock drift
                },
                out _
            );
            return principal;
        }
        catch
        {
            // Deliberately swallow — invalid tokens return null
            return null;
        }
    }
}
