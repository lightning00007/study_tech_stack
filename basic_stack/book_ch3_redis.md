# Chapter 3: Redis Cache — In-Memory Data Structure Store

---

## 3.1 What Is Redis?

Redis stands for **Remote Dictionary Server**. It was created by Salvatore Sanfilippo in 2009. Redis is an **in-memory data structure store** — it holds all data in RAM and provides sub-millisecond response times.

Redis is commonly used as:
- **Cache**: Store expensive query results temporarily
- **Session store**: Stateless applications share session data across instances
- **Distributed lock**: Coordinate access to a shared resource across multiple servers
- **Rate limiter**: Count and limit API requests per user
- **Message broker**: Simple pub/sub messaging, job queues
- **Leaderboard**: Sorted sets make ranking trivially easy
- **Real-time analytics**: Counters, time-windowed statistics

### Redis vs Traditional Cache (Local Memory Cache)

| | Local Memory Cache (IMemoryCache) | Redis (IDistributedCache) |
|---|---|---|
| **Shared across servers?** | ❌ No — each server has its own cache | ✅ Yes — all servers share one cache |
| **Survives app restart?** | ❌ No | ✅ Yes (with persistence enabled) |
| **Data types** | Just objects serialized | Rich: strings, lists, sets, hashes, sorted sets, streams |
| **Eviction** | LRU automatic | Configurable policies |
| **Use case** | Single-server apps | Distributed / multi-instance apps |

---

## 3.2 Redis Data Types — The Core of Redis's Power

Every Redis key holds one of several data types. Choosing the right type is the key to effective Redis usage.

### 3.2.1 Strings — The Simplest Type

A string can hold text, serialized JSON, numbers, or binary data. Maximum size: 512MB.

```bash
# Set and Get
SET user:42:name "Alice Nguyen"
GET user:42:name           # "Alice Nguyen"

# Set with expiry (TTL = Time To Live)
SET session:abc123 '{"userId":42}' EX 3600   # expires in 3600 seconds (1 hour)
SET session:abc123 '{"userId":42}' PX 3600000 # expires in milliseconds

# Set only if NOT exists (useful for distributed locks)
SET lock:resource1 "server-1" NX EX 30    # NX = only set if Not eXists
# Returns OK if acquired, nil if already held by someone else

# Set only if EXISTS (update without creating)
SET user:42:name "Alice Smith" XX

# Get remaining TTL
TTL session:abc123    # returns seconds remaining (-1 = no expiry, -2 = key doesn't exist)
PTTL session:abc123   # returns milliseconds

# Atomic counter operations (thread-safe, no race conditions)
SET api:hits:user:42 0
INCR api:hits:user:42    # atomically increment by 1 → returns 1
INCR api:hits:user:42    # → returns 2
INCRBY api:hits:user:42 10   # increment by 10 → returns 12
DECR api:hits:user:42
DECRBY api:hits:user:42 5

# Store and retrieve numeric floats
SET product:1:price 99.99
INCRBYFLOAT product:1:price 10.50   # → 110.49

# Append to string
APPEND user:42:log "login@10:00 "
APPEND user:42:log "logout@10:30 "
GET user:42:log    # "login@10:00 logout@10:30 "
```

### 3.2.2 Lists — Ordered Collections

A list is a sequence of strings, ordered by insertion order. Efficient O(1) push/pop from both ends.

```bash
# Push to the left (head) or right (tail)
RPUSH notifications:user:42 "Order shipped"    # push right
RPUSH notifications:user:42 "Payment received"
LPUSH notifications:user:42 "New message"      # push left (becomes first)

# Range (0 to -1 means all elements)
LRANGE notifications:user:42 0 -1
# 1) "New message"
# 2) "Order shipped"
# 3) "Payment received"

# Length
LLEN notifications:user:42    # 3

# Pop from left or right (queue vs stack)
LPOP notifications:user:42    # "New message" — queue: FIFO with LPUSH + RPOP
RPOP notifications:user:42    # "Payment received" — stack: LIFO with RPUSH + RPOP

# Blocking pop — wait up to 30 seconds for a new item (great for job queues)
BLPOP job:queue 30    # blocks until a job arrives or 30 seconds pass

# Trim list to specific range (e.g., keep only last 100 notifications)
LTRIM notifications:user:42 0 99

# Use case: Job Queue
RPUSH job:email-queue '{"to":"user@example.com","subject":"Welcome"}'  # producer
BLPOP job:email-queue 0    # consumer: blocks until a job arrives
```

### 3.2.3 Hashes — Objects / Maps

A hash is a map of field-value pairs — like a JSON object or a dictionary.

```bash
# Set fields
HSET user:42 name "Alice" email "alice@example.com" role "admin" loginCount 0

# Get one field
HGET user:42 email      # "alice@example.com"

# Get all fields
HGETALL user:42
# 1) "name"  2) "Alice"
# 3) "email" 4) "alice@example.com"
# 5) "role"  6) "admin"
# 7) "loginCount" 8) "0"

# Get multiple specific fields
HMGET user:42 name role   # ["Alice", "admin"]

# Check if field exists
HEXISTS user:42 email    # 1

# Increment a numeric field
HINCRBY user:42 loginCount 1    # → 1

# Delete a field
HDEL user:42 role

# Get all keys or all values
HKEYS user:42    # ["name", "email", "loginCount"]
HVALS user:42    # ["Alice", "alice@example.com", "1"]

# Why use Hash instead of JSON String?
# With JSON String: to update one field, you GET, deserialize, modify, serialize, SET — wasteful
# With Hash: HINCRBY user:42 loginCount 1 — atomic, no serialize/deserialize needed
```

### 3.2.4 Sets — Unique Unordered Collections

A set is an unordered collection of unique strings. Supports powerful set operations.

```bash
# Add members
SADD user:42:tags "vip" "newsletter" "beta-tester"
SADD user:43:tags "vip" "newsletter"

# Check membership
SISMEMBER user:42:tags "vip"      # 1 (yes)
SISMEMBER user:42:tags "admin"    # 0 (no)

# All members
SMEMBERS user:42:tags    # {"vip", "newsletter", "beta-tester"}

# Count
SCARD user:42:tags    # 3

# Remove
SREM user:42:tags "beta-tester"

# Set operations
SINTER user:42:tags user:43:tags    # intersection: {"vip", "newsletter"}
SUNION user:42:tags user:43:tags    # union: {"vip", "newsletter", "beta-tester"}
SDIFF  user:42:tags user:43:tags    # difference (in 42 but not 43): {"beta-tester"}

# Store result of set operation
SINTERSTORE common:tags user:42:tags user:43:tags

# Use case: online users tracking
SADD online:users 42
SADD online:users 99
SCARD online:users    # how many users are online?
SISMEMBER online:users 42    # is user 42 online?
```

### 3.2.5 Sorted Sets (ZSet) — The Leaderboard Type

A sorted set is like a set but each member has an associated **score** (a float). Members are sorted by score. This is Redis's most versatile data type.

```bash
# Add members with scores
ZADD leaderboard 1500 "alice"
ZADD leaderboard 2200 "bob"
ZADD leaderboard 1800 "charlie"
ZADD leaderboard 2200 "diana"    # same score as bob — sorted alphabetically

# Get rank (0-indexed, lowest score first)
ZRANK leaderboard "alice"        # 0 (lowest score)
ZREVRANK leaderboard "bob"       # 0 (highest score — alice is rank 0 from bottom, bob is rank 0 from top)

# Get score
ZSCORE leaderboard "alice"    # "1500"

# Get top 3 players (reverse = descending score)
ZREVRANGE leaderboard 0 2 WITHSCORES
# 1) "diana"   2) "2200"
# 3) "bob"     4) "2200"
# 5) "charlie" 6) "1800"

# Range by score
ZRANGEBYSCORE leaderboard 1500 2000    # members with score between 1500 and 2000

# Increment score (atomic)
ZINCRBY leaderboard 100 "alice"    # alice's score → 1600

# Count members in score range
ZCOUNT leaderboard 1500 2000    # 2 (alice and charlie)

# Use case: Rate limiting with sliding window
# Score = timestamp, member = unique request ID
ZADD ratelimit:user:42 1704067200 "req1"
ZADD ratelimit:user:42 1704067201 "req2"
# Remove requests older than 1 minute
ZREMRANGEBYSCORE ratelimit:user:42 0 (current_time - 60)
# Count recent requests
ZCARD ratelimit:user:42
```

### 3.2.6 Streams — Persistent Message Log

Streams (added in Redis 5.0) are an append-only log structure, similar to Apache Kafka. Unlike Pub/Sub, messages are **persistent** and **consumers can catch up from where they left off**.

```bash
# Add messages to a stream
XADD orders:stream * event OrderCreated orderId 42 userId 1 total 150.00
# * = auto-generate message ID (timestamp-sequence)
# Returns: "1704067200000-0" (millisecond timestamp - sequence)

# Read from beginning
XREAD COUNT 10 STREAMS orders:stream 0

# Read only new messages (from now on)
XREAD COUNT 10 BLOCK 0 STREAMS orders:stream $

# Consumer groups (multiple consumers, each gets different messages)
XGROUP CREATE orders:stream email-workers $ MKSTREAM
XGROUP CREATE orders:stream inventory-workers $

# Consumer reads from group
XREADGROUP GROUP email-workers consumer-1 COUNT 10 STREAMS orders:stream >
# '>' means: give me messages not yet delivered to this group

# Acknowledge processed message (removes from pending list)
XACK orders:stream email-workers 1704067200000-0
```

---

## 3.3 Redis Persistence

Redis is primarily in-memory, but it offers two persistence options so data survives restarts:

### 3.3.1 RDB (Redis Database) — Snapshots

RDB periodically saves a point-in-time snapshot of all data to a `.rdb` file.

```bash
# redis.conf settings
save 900 1      # save if at least 1 key changed in 900 seconds
save 300 10     # save if at least 10 keys changed in 300 seconds
save 60 10000   # save if at least 10000 keys changed in 60 seconds

dbfilename dump.rdb
dir /var/lib/redis
```

**Pros**: Compact file, fast startup, great for backups
**Cons**: Data loss between last snapshot and crash (RPO = minutes)

### 3.3.2 AOF (Append Only File) — Write Log

AOF logs every write command to a file. On restart, Redis replays all commands.

```bash
# redis.conf settings
appendonly yes
appendfilename "appendonly.aof"

# Sync frequency
appendfsync always      # sync after every write — safest, slowest
appendfsync everysec    # sync every second — good balance (default)
appendfsync no          # let OS decide — fastest, highest risk

# AOF rewrite (compacts AOF file by eliminating redundant commands)
auto-aof-rewrite-percentage 100
auto-aof-rewrite-min-size 64mb
```

**Pros**: Much lower data loss (at most 1 second with `everysec`)
**Cons**: Larger files, slower startup, slightly lower performance

**Best practice for production**: Enable **both** RDB + AOF:
```bash
appendonly yes
save 900 1
```

---

## 3.4 Eviction Policies

When Redis reaches `maxmemory`, it must decide what to evict:

```bash
# redis.conf
maxmemory 2gb
maxmemory-policy allkeys-lru
```

| Policy | Behavior |
|---|---|
| `noeviction` | Returns error when memory full. Safe — nothing is deleted. |
| `allkeys-lru` | Evict the **least recently used** key from ALL keys. **Best for general cache.** |
| `allkeys-lfu` | Evict the **least frequently used** key from ALL keys. |
| `volatile-lru` | Evict LRU key but only from keys **with an expiry set** |
| `volatile-ttl` | Evict key with the **shortest remaining TTL** |
| `volatile-random` | Evict random key from keys with expiry |
| `allkeys-random` | Evict any random key |

**Rule of thumb**: Use `allkeys-lru` for a pure cache. Use `noeviction` for session storage (you don't want sessions silently evicted).

---

## 3.5 High Availability

### 3.5.1 Redis Sentinel

Sentinel provides **automatic failover** for a single Redis setup:

```
[Redis Primary]
      │
      ├──► [Redis Replica 1]
      └──► [Redis Replica 2]

[Sentinel 1] ─┐
[Sentinel 2] ─┼── Monitor primary, vote on failover
[Sentinel 3] ─┘
```

When primary fails: Sentinels vote, elect a new primary, reconfigure replicas, and notify clients.

### 3.5.2 Redis Cluster

Redis Cluster shards data across **multiple primary nodes** for horizontal scaling:

```
[Primary 1: Slots 0-5460]     + [Replica 1a] [Replica 1b]
[Primary 2: Slots 5461-10922] + [Replica 2a] [Replica 2b]
[Primary 3: Slots 10923-16383]+ [Replica 3a] [Replica 3b]
```

Keys are distributed using **consistent hashing** (16384 hash slots). The client library handles routing automatically.

```csharp
// StackExchange.Redis handles cluster transparently
var connection = ConnectionMultiplexer.Connect("node1:6379,node2:6379,node3:6379");
var db = connection.GetDatabase();
await db.StringSetAsync("mykey", "value");  // routed to correct shard automatically
```

---

## 3.6 Pub/Sub Messaging

Redis Pub/Sub is fire-and-forget messaging. Publishers send messages to channels; subscribers receive them. **Messages are not persisted** — if no subscriber is listening, the message is lost.

```bash
# Subscriber (in one terminal)
SUBSCRIBE notifications

# Publisher (in another terminal)
PUBLISH notifications "User 42 just logged in"
# Subscriber receives: "User 42 just logged in"

# Pattern subscription
PSUBSCRIBE orders:*    # receives messages from orders:created, orders:updated, etc.
PUBLISH orders:created '{"orderId": 42}'
```

---

## 3.7 Redis in .NET — Complete Patterns

### 3.7.1 Setup and Connection

```csharp
// Install:
// dotnet add package StackExchange.Redis
// dotnet add package Microsoft.Extensions.Caching.StackExchangeRedis

// Program.cs — register IDistributedCache (high-level, string-based)
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = builder.Configuration.GetConnectionString("Redis");
    options.InstanceName = "MyApp:";  // all keys prefixed with "MyApp:"
});

// Also register IConnectionMultiplexer for low-level access
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
    ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("Redis")!));

// Redis connection string formats:
// Local:  "localhost:6379"
// With password: "localhost:6379,password=secret"
// Redis Cloud: "redis-12345.c1.us-east-1-4.ec2.cloud.redislabs.com:12345,password=xxx,ssl=true"
// AWS ElastiCache: "my-redis.xxxxxx.0001.use1.cache.amazonaws.com:6379"
```

### 3.7.2 Cache Service with Generic Methods

```csharp
public interface ICacheService
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);
    Task RemoveByPatternAsync(string pattern, CancellationToken ct = default);
    Task<T> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan? expiry = null, CancellationToken ct = default);
}

public class RedisCacheService : ICacheService
{
    private readonly IDistributedCache _distributedCache;
    private readonly IConnectionMultiplexer _redis;
    private readonly JsonSerializerOptions _jsonOptions;

    public RedisCacheService(IDistributedCache distributedCache, IConnectionMultiplexer redis)
    {
        _distributedCache = distributedCache;
        _redis = redis;
        _jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        var json = await _distributedCache.GetStringAsync(key, ct);
        if (json is null) return default;

        return JsonSerializer.Deserialize<T>(json, _jsonOptions);
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(value, _jsonOptions);
        var options = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = expiry ?? TimeSpan.FromMinutes(30)
        };
        await _distributedCache.SetStringAsync(key, json, options, ct);
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
        => await _distributedCache.RemoveAsync(key, ct);

    // Remove all keys matching a pattern (e.g., "user:42:*")
    // Note: SCAN is preferred over KEYS in production (KEYS blocks Redis)
    public async Task RemoveByPatternAsync(string pattern, CancellationToken ct = default)
    {
        var server = _redis.GetServer(_redis.GetEndPoints().First());
        var db = _redis.GetDatabase();

        await foreach (var key in server.KeysAsync(pattern: pattern))
        {
            await db.KeyDeleteAsync(key);
        }
    }

    // Get from cache OR compute and store
    public async Task<T> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan? expiry = null, CancellationToken ct = default)
    {
        var cached = await GetAsync<T>(key, ct);
        if (cached is not null) return cached;

        var value = await factory();
        await SetAsync(key, value, expiry, ct);
        return value;
    }
}
```

### 3.7.3 Cache-Aside Pattern (Most Common)

```csharp
public class ProductService
{
    private readonly IProductRepository _repo;
    private readonly ICacheService _cache;

    private static string CacheKey(int id) => $"product:{id}";

    public async Task<ProductDto?> GetProductAsync(int id, CancellationToken ct = default)
    {
        // The GetOrSetAsync encapsulates the cache-aside pattern completely
        return await _cache.GetOrSetAsync(
            key: CacheKey(id),
            factory: async () =>
            {
                var product = await _repo.GetByIdAsync(id, ct);
                return product is null ? null : new ProductDto(product.Id, product.Name, product.Price);
            },
            expiry: TimeSpan.FromHours(1),
            ct: ct
        );
    }

    public async Task UpdateProductAsync(int id, UpdateProductRequest request, CancellationToken ct = default)
    {
        var product = await _repo.GetByIdAsync(id, ct) ?? throw new NotFoundException($"Product {id} not found");

        product.Name = request.Name;
        product.Price = request.Price;

        await _repo.UpdateAsync(product);
        await _unitOfWork.SaveChangesAsync(ct);

        // CRITICAL: Invalidate cache after update
        await _cache.RemoveAsync(CacheKey(id), ct);
        // Next request will get fresh data from DB
    }
}
```

### 3.7.4 Distributed Lock (RedLock Algorithm)

A distributed lock ensures only one instance of your application runs a critical section at a time.

```csharp
// Install: dotnet add package RedLock.net
// Program.cs
builder.Services.AddSingleton<IDistributedLockFactory>(sp =>
{
    var multiplexer = sp.GetRequiredService<IConnectionMultiplexer>();
    return RedLockFactory.Create(new List<RedLockMultiplexer> { new(multiplexer) });
});

// Usage: only one server processes this at a time
public class PaymentProcessor
{
    private readonly IDistributedLockFactory _lockFactory;

    public async Task ProcessPaymentAsync(int orderId, CancellationToken ct = default)
    {
        var lockKey = $"payment:lock:{orderId}";
        var expiry = TimeSpan.FromSeconds(30);
        var wait = TimeSpan.FromSeconds(10);
        var retry = TimeSpan.FromMilliseconds(500);

        await using var redLock = await _lockFactory.CreateLockAsync(lockKey, expiry, wait, retry, ct);

        if (!redLock.IsAcquired)
        {
            throw new InvalidOperationException($"Could not acquire lock for order {orderId}. Already being processed.");
        }

        // Critical section — only ONE server runs this at a time
        await ProcessPaymentInternalAsync(orderId, ct);
    }
}
```

### 3.7.5 Rate Limiting with Redis

```csharp
public class RateLimiter
{
    private readonly IDatabase _db;

    public RateLimiter(IConnectionMultiplexer redis) => _db = redis.GetDatabase();

    // Fixed Window: N requests per window (e.g., 100 requests per minute)
    public async Task<(bool Allowed, int Remaining)> CheckFixedWindowAsync(
        string key, int limit, TimeSpan window)
    {
        var fullKey = $"ratelimit:fixed:{key}";

        var current = await _db.StringIncrementAsync(fullKey);

        if (current == 1)
        {
            // First request in window — set expiry
            await _db.KeyExpireAsync(fullKey, window);
        }

        var allowed = current <= limit;
        var remaining = Math.Max(0, limit - (int)current);

        return (allowed, remaining);
    }

    // Sliding Window: More accurate, uses Sorted Set
    public async Task<(bool Allowed, int Remaining)> CheckSlidingWindowAsync(
        string key, int limit, TimeSpan window)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var windowStart = now - (long)window.TotalMilliseconds;
        var fullKey = $"ratelimit:sliding:{key}";

        var transaction = _db.CreateTransaction();

        // Remove expired requests
        _ = transaction.SortedSetRemoveRangeByScoreAsync(fullKey, 0, windowStart);
        // Add current request
        _ = transaction.SortedSetAddAsync(fullKey, Guid.NewGuid().ToString(), now);
        // Count requests in window
        var countTask = transaction.SortedSetLengthAsync(fullKey);
        // Set key expiry
        _ = transaction.KeyExpireAsync(fullKey, window);

        await transaction.ExecuteAsync();

        var count = (int)await countTask;
        var allowed = count <= limit;
        var remaining = Math.Max(0, limit - count);

        return (allowed, remaining);
    }
}

// Middleware usage
public class RateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly RateLimiter _limiter;

    public async Task InvokeAsync(HttpContext context)
    {
        var userKey = context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";

        var (allowed, remaining) = await _limiter.CheckSlidingWindowAsync(
            key: $"api:{userKey}",
            limit: 100,
            window: TimeSpan.FromMinutes(1)
        );

        context.Response.Headers.Append("X-RateLimit-Limit", "100");
        context.Response.Headers.Append("X-RateLimit-Remaining", remaining.ToString());

        if (!allowed)
        {
            context.Response.StatusCode = 429; // Too Many Requests
            await context.Response.WriteAsJsonAsync(new { error = "Rate limit exceeded. Try again in 1 minute." });
            return;
        }

        await _next(context);
    }
}
```

### 3.7.6 Pub/Sub in .NET

```csharp
// Publisher
public class EventPublisher
{
    private readonly IConnectionMultiplexer _redis;

    public async Task PublishAsync<T>(string channel, T message)
    {
        var subscriber = _redis.GetSubscriber();
        var json = JsonSerializer.Serialize(message);
        await subscriber.PublishAsync(RedisChannel.Literal(channel), json);
    }
}

// Subscriber (registered as a hosted service)
public class OrderEventSubscriber : IHostedService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IServiceScopeFactory _scopeFactory;
    private ISubscriber? _subscriber;

    public async Task StartAsync(CancellationToken ct)
    {
        _subscriber = _redis.GetSubscriber();

        await _subscriber.SubscribeAsync(
            RedisChannel.Pattern("orders:*"),
            async (channel, message) =>
            {
                using var scope = _scopeFactory.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<IOrderEventHandler>();

                var payload = JsonSerializer.Deserialize<OrderEvent>(message!);
                await handler.HandleAsync(payload!, ct);
            }
        );
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_subscriber is not null)
            await _subscriber.UnsubscribeAllAsync();
    }
}
```

---

## 3.8 Redis Key Naming Conventions

Good key naming is critical for organization and preventing collisions:

```
# Pattern: object-type:id:field
user:42                         → hash of user 42's profile
user:42:sessions                → set of active session IDs
user:42:orders                  → list of order IDs
session:abc123                  → session token data
product:99:price                → current price
product:99:views                → view counter
leaderboard:2024-06             → monthly leaderboard
ratelimit:api:192.168.1.1       → rate limit counter
lock:payment:order:42           → distributed lock
cache:query:products:page:1     → cached query result
```

Rules:
1. Use colons `:` as separators
2. Hierarchy: `type:id:attribute`
3. Keep keys short but readable
4. Always set a TTL for cache keys — never let cache keys live forever

---

## Summary

Redis is an incredibly versatile tool. The key insights are:

1. **Choose the right data type** — Strings for simple cache, Hashes for objects, Sorted Sets for rankings/rate limiting, Lists for queues, Sets for unique membership
2. **Always set a TTL** on cache keys — unbounded caches eventually exhaust memory
3. **Cache-Aside is the primary pattern** — try cache first, load from DB on miss, invalidate on write
4. **Distributed locks require careful design** — always set expiry, handle lock acquisition failure
5. **Eviction policy matters** — `allkeys-lru` for cache, `noeviction` for sessions
6. **Pub/Sub is fire-and-forget** — use Streams if you need durability
7. **Sliding window rate limiting with Sorted Sets** is the most accurate approach
