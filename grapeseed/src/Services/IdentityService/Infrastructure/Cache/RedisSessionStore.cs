using System.Text.Json;
using StackExchange.Redis;

namespace GrapeSeed.IdentityService.Infrastructure.Cache;

// =============================================================================
// 📖 CONCEPT: Redis Session Store for JWT Invalidation
// =============================================================================
// JWTs are stateless — once issued, they're valid until they expire.
// This creates a problem: what if a student logs out or an account is compromised?
// The JWT is still valid for up to 1 hour.
//
// Solution: The Redis Session Store.
// When we issue a JWT, we store a session record in Redis keyed by the JWT's
// unique ID (jti claim). Every protected API request checks:
//   1. Is the JWT signature valid? (standard JWT validation)
//   2. Does a session record exist in Redis for this jti?
//
// If the session is missing (deleted on logout or suspension), the request fails
// with 401 Unauthorized — even if the JWT itself is cryptographically valid.
//
// This gives us stateful logout while keeping the benefits of stateless JWT
// validation (no DB lookup for each request — Redis is in-memory and very fast).
//
// Key schema: "session:{tenantId}:{studentId}:{jti}"
// TTL: matches the JWT expiry (1 hour)
//
// 🔗 SEE ALSO: docs/06-redis-and-postgres.md#65-cache-aside-pattern
// =============================================================================

/// <summary>Contract for managing JWT sessions in Redis.</summary>
public interface IRedisSessionStore
{
    Task StoreSessionAsync(
        Guid studentId, Guid tenantId, string jti, DateTime expiry,
        CancellationToken cancellationToken = default);

    Task<bool> SessionExistsAsync(
        Guid studentId, Guid tenantId, string jti,
        CancellationToken cancellationToken = default);

    Task RevokeSessionAsync(
        Guid studentId, Guid tenantId, string jti,
        CancellationToken cancellationToken = default);

    Task RevokeAllSessionsAsync(
        Guid studentId, Guid tenantId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Redis implementation of the JWT session store.
/// Uses StackExchange.Redis for high-performance session management.
/// </summary>
public sealed class RedisSessionStore : IRedisSessionStore
{
    private readonly IDatabase _redis;

    public RedisSessionStore(IConnectionMultiplexer connectionMultiplexer)
    {
        // 📖 CONCEPT: IConnectionMultiplexer is a long-lived, thread-safe object.
        // Register it as Singleton in DI. GetDatabase() is cheap — it reuses the connection.
        _redis = connectionMultiplexer.GetDatabase();
    }

    public async Task StoreSessionAsync(
        Guid studentId, Guid tenantId, string jti, DateTime expiry,
        CancellationToken cancellationToken = default)
    {
        var key = BuildSessionKey(tenantId, studentId, jti);
        var sessionData = JsonSerializer.Serialize(new SessionRecord(studentId, tenantId, jti, expiry));
        var ttl = expiry - DateTime.UtcNow;

        // 📖 CONCEPT: Redis StringSetAsync with expiry
        // The TTL (time-to-live) makes Redis automatically delete the key when the
        // JWT expires. We don't need a cleanup job for expired sessions.
        await _redis.StringSetAsync(key, sessionData, ttl);

        // 📖 CONCEPT: Tracking all sessions for a student (for "logout everywhere")
        // We maintain a Redis Set of all active jti values for each student.
        // This allows us to delete ALL sessions when the student logs out from all devices.
        var studentSessionsKey = BuildStudentSessionsKey(tenantId, studentId);
        await _redis.SetAddAsync(studentSessionsKey, jti);
        await _redis.KeyExpireAsync(studentSessionsKey, TimeSpan.FromDays(30)); // rolling 30-day max
    }

    public async Task<bool> SessionExistsAsync(
        Guid studentId, Guid tenantId, string jti,
        CancellationToken cancellationToken = default)
    {
        var key = BuildSessionKey(tenantId, studentId, jti);
        // 📖 CONCEPT: KeyExistsAsync is O(1) — extremely fast regardless of data size
        return await _redis.KeyExistsAsync(key);
    }

    public async Task RevokeSessionAsync(
        Guid studentId, Guid tenantId, string jti,
        CancellationToken cancellationToken = default)
    {
        var key = BuildSessionKey(tenantId, studentId, jti);
        await _redis.KeyDeleteAsync(key);

        var studentSessionsKey = BuildStudentSessionsKey(tenantId, studentId);
        await _redis.SetRemoveAsync(studentSessionsKey, jti);
    }

    public async Task RevokeAllSessionsAsync(
        Guid studentId, Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        // ── Logout from all devices ────────────────────────────────────────
        // 📖 CONCEPT: "Logout everywhere" — revoke all active sessions.
        // Used when: account suspension, password reset, or security incident.
        var studentSessionsKey = BuildStudentSessionsKey(tenantId, studentId);
        var allJtis = await _redis.SetMembersAsync(studentSessionsKey);

        // Delete each individual session key
        var sessionKeys = allJtis
            .Select(jti => (RedisKey)BuildSessionKey(tenantId, studentId, jti.ToString()))
            .ToArray();

        if (sessionKeys.Length > 0)
        {
            // 📖 CONCEPT: KeyDeleteAsync with multiple keys is atomic and efficient.
            // Deletes all session keys in a single Redis command.
            await _redis.KeyDeleteAsync(sessionKeys);
        }

        await _redis.KeyDeleteAsync(studentSessionsKey);
    }

    // ── Key builders ──────────────────────────────────────────────────────────
    // 📖 CONCEPT: Redis key naming conventions
    // Keys are namespaced with colons: category:tenant:entity:attribute
    // This makes it easy to find related keys and prevents collisions.

    private static string BuildSessionKey(Guid tenantId, Guid studentId, string jti)
        => $"session:{tenantId}:{studentId}:{jti}";

    private static string BuildStudentSessionsKey(Guid tenantId, Guid studentId)
        => $"student_sessions:{tenantId}:{studentId}";

    private sealed record SessionRecord(Guid StudentId, Guid TenantId, string Jti, DateTime Expiry);
}
