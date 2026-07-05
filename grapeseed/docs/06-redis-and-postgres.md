# Chapter 6 — Redis and PostgreSQL

> *"In caching, the two hardest problems are cache invalidation and naming things."*  
> — Adapted from Phil Karlton

---

## 6.1 PostgreSQL: The Relational Backbone

PostgreSQL is GrapeSeed's primary data store. It is chosen over other databases because:

- **ACID transactions**: Every multi-step database operation (e.g., creating a tenant and their
  schema in one go) is atomic. Partial failures leave no orphaned data.
- **Rich schema support**: PostgreSQL schemas are a first-class feature, enabling the
  per-tenant isolation strategy without separate databases.
- **JSONB columns**: Some semi-structured data (e.g., video metadata, recommendation context)
  is stored as JSONB, giving relational structure where needed and document flexibility where helpful.
- **Full-text search**: Native support for fuzzy search across video titles and descriptions
  without an additional search engine for early-stage products.
- **Row-Level Security (RLS)**: An additional safety net for the shared-schema approach
  (not used by default in GrapeSeed, but documented as a production consideration).

---

## 6.2 PostgreSQL Schema Design

### Shared Schema (managed by TenantService)

```sql
-- Schema: public (or 'shared')
-- This is the global registry. TenantService owns it.

CREATE TABLE tenants (
    id                          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name                        TEXT NOT NULL,
    slug                        TEXT NOT NULL UNIQUE,    -- used as schema name prefix
    email                       TEXT NOT NULL UNIQUE,
    plan_id                     TEXT NOT NULL,
    subscription_fee_amount     DECIMAL(10,2) NOT NULL,
    subscription_fee_currency   TEXT NOT NULL DEFAULT 'USD',
    stripe_customer_id          TEXT,
    status                      TEXT NOT NULL DEFAULT 'pending',  -- pending|active|suspended
    created_at                  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE outbox_messages (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    event_type      TEXT NOT NULL,
    payload         JSONB NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    processed_at    TIMESTAMPTZ,
    error           TEXT
);
```

### Tenant Schema (created per tenant, managed by each service)

```sql
-- Schema: tenant_{slug}   e.g., tenant_schoola
-- Each service migrates its own tables in this schema.

-- IdentityService tables:
CREATE TABLE students (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    email           TEXT NOT NULL UNIQUE,
    password_hash   TEXT NOT NULL,
    full_name       TEXT NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE refresh_tokens (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    student_id  UUID NOT NULL REFERENCES students(id),
    token_hash  TEXT NOT NULL UNIQUE,
    expires_at  TIMESTAMPTZ NOT NULL,
    revoked_at  TIMESTAMPTZ
);

-- VideoService tables:
CREATE TABLE videos (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    title           TEXT NOT NULL,
    description     TEXT,
    s3_key          TEXT NOT NULL,              -- raw upload location
    cloudfront_key  TEXT,                       -- processed HLS path
    status          TEXT NOT NULL DEFAULT 'uploading', -- uploading|processing|ready|failed
    duration_seconds INT,
    metadata        JSONB,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- RecommendationService tables:
CREATE TABLE watch_history (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    student_id      UUID NOT NULL,
    video_id        UUID NOT NULL,
    watched_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    watch_percentage DECIMAL(5,2),              -- 0.00 to 100.00
    UNIQUE (student_id, video_id, watched_at)
);
```

---

## 6.3 PostgreSQL Indexing Strategy

An index is a data structure that allows the database to find rows quickly without scanning
every row in the table. The wrong indexing strategy (too few or too many indexes) is one
of the most common causes of slow PostgreSQL queries.

```sql
-- 📖 CONCEPT: Composite index for the recommendation query
-- This query runs frequently: "find all videos watched by student X"
-- Without an index, Postgres scans every row in watch_history.
-- With this index, it jumps directly to the relevant rows.
CREATE INDEX idx_watch_history_student ON watch_history(student_id, watched_at DESC);

-- 📖 CONCEPT: Partial index for pending outbox messages
-- 99% of outbox_messages have been processed. A full index would waste space.
-- This partial index only indexes the unprocessed messages — exactly what the
-- background job queries every few seconds.
CREATE INDEX idx_outbox_unprocessed ON outbox_messages(created_at)
    WHERE processed_at IS NULL;

-- ⚠️ GOTCHA: Be careful with indexes on high-write tables.
-- Every INSERT/UPDATE/DELETE must also update all indexes.
-- Too many indexes on watch_history (a high-write table) can slow down recording watches.
```

---

## 6.4 Redis: The High-Speed Cache

Redis is an in-memory data structure store. It can be used as a cache, a session store, a
message broker (Pub/Sub), and more. In GrapeSeed, Redis serves two purposes:

1. **JWT Session Store** (IdentityService): Valid JWT tokens are stored in Redis with a TTL
   matching the token expiry. When a student logs out, the token is immediately deleted from
   Redis — rendering it invalid even if it hasn't expired yet.

2. **Recommendation Cache** (RecommendationService): Building a personalised recommendation
   list requires computing a ranking over the student's watch history. This is expensive.
   The result is cached in Redis for 5 minutes.

---

## 6.5 Redis Data Structures

Redis is not just a key-value store. It supports rich data structures:

### Strings (JWT session store)

```
Key:   session:{studentId}:{jti}       (jti = JWT ID claim, unique per token)
Value: "{\"studentId\":\"...\",\"tenantId\":\"...\",\"issuedAt\":\"...\"}"
TTL:   3600 seconds (1 hour — matching the JWT expiry)
```

```csharp
// 📖 CONCEPT: Redis string for JWT invalidation
// When a student logs out, we delete this key. Any subsequent request with
// the same JWT will fail the session check, even if the JWT signature is valid.
await _redis.StringSetAsync(
    key: $"session:{studentId}:{jti}",
    value: JsonSerializer.Serialize(sessionData),
    expiry: TimeSpan.FromHours(1)
);
```

### Sorted Sets (recommendation ranking)

A Redis Sorted Set stores members with a score. Members are automatically ordered by score.
This is ideal for "top N videos for student X":

```
Key:    recs:{tenantId}:{studentId}
Members: videoId → score (e.g., recommendation confidence: 0.0 to 1.0)

ZADD recs:schoola:student-123 0.95 "vid-001"   (high confidence)
ZADD recs:schoola:student-123 0.72 "vid-002"
ZADD recs:schoola:student-123 0.45 "vid-003"

ZREVRANGE recs:schoola:student-123 0 9  → top 10 videos by score
```

```csharp
// 📖 CONCEPT: Redis Sorted Set for recommendations
// ZREVRANGE returns members in descending order (highest score first).
// We fetch the top 10 recommended video IDs from Redis in O(log N) time.
var topVideoIds = await _redis.SortedSetRangeByRankAsync(
    key: $"recs:{tenantId}:{studentId}",
    start: 0,
    stop: 9,
    order: Order.Descending
);
```

---

## 6.6 Cache-Aside Pattern

GrapeSeed uses the **Cache-Aside** (Lazy Loading) pattern: the application code manages the cache,
rather than the cache being automatically populated by some framework:

```
1. Application requests data
        │
        ▼
2. Check Redis cache
        │
   ┌────┴────┐
   │ HIT     │ MISS
   │         ▼
   │   3. Query PostgreSQL
   │         │
   │   4. Store result in Redis (with TTL)
   │         │
   └────►────┘
        │
        ▼
5. Return data to caller
```

```csharp
// 📖 CONCEPT: Cache-Aside in the GetRecommendationsQueryHandler
var cacheKey = $"recs:{tenantId}:{studentId}";
var cached = await _redis.StringGetAsync(cacheKey);

if (cached.HasValue)
{
    // Cache HIT — deserialise and return without touching PostgreSQL
    return JsonSerializer.Deserialize<List<VideoRecommendation>>(cached!);
}

// Cache MISS — compute from watch history in PostgreSQL
var recommendations = await ComputeRecommendationsAsync(studentId, tenantId);

// Populate the cache for next time (TTL: 5 minutes)
await _redis.StringSetAsync(
    key: cacheKey,
    value: JsonSerializer.Serialize(recommendations),
    expiry: TimeSpan.FromMinutes(5)
);

return recommendations;
```

---

## 6.7 Cache Invalidation: The Hard Part

When a student watches a new video, their cached recommendations become stale — the cache
still reflects their old watch history. GrapeSeed handles this in two ways:

1. **TTL-based expiry (simple)**: The cache expires every 5 minutes. Recommendations are
   "good enough" even if slightly stale. This is acceptable for most users.

2. **Explicit invalidation (precise)**: When `WatchHistoryUpdatedEvent` arrives (via SQS),
   RecommendationService deletes the student's cache entry immediately:

```csharp
// 📖 CONCEPT: Explicit cache invalidation on domain event
// This ensures the next recommendation request always gets fresh data
// right after the student finishes watching a video.
await _redis.KeyDeleteAsync($"recs:{tenantId}:{studentId}");
```

> ⚠️ **GOTCHA: Cache Stampede**
> If 1,000 students finish watching the same popular video simultaneously, all their cache
> entries are invalidated at once. Suddenly, 1,000 concurrent requests hit PostgreSQL to
> recompute recommendations. This is called a *cache stampede* (or thundering herd).
>
> The fix: use a **mutex lock** in Redis (`SET key value NX EX 5`) to ensure only one request
> recomputes for a given student at a time. All other requests wait and then read the freshly
> populated cache.

---

*This concludes the documentation chapters. Now explore the source code in `src/` to see these concepts in action!*
