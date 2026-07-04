# Chapter 3: High Load Systems

> **Performance Engineering · Scalability · High Availability**
> *"Any sufficiently popular system will eventually be crushed under its own success. The engineers who survive this know it's coming and prepare."*

---

## Table of Contents

1. [Introduction — The Day the Platform Stood Still](#1-introduction)
2. [What Is High Load? The Numbers That Matter](#2-what-is-high-load)
3. [The Bottleneck Hunt — Finding What Breaks First](#3-bottleneck-hunt)
4. [Scaling Strategies — Going Up vs. Going Wide](#4-scaling-strategies)
5. [Load Balancing — Sharing the Work Fairly](#5-load-balancing)
6. [The Caching Hierarchy — From CDN to Database](#6-caching-hierarchy)
7. [Database at Scale — Read Replicas and Sharding](#7-database-at-scale)
8. [Connection Pooling — Don't Waste Your Database Connections](#8-connection-pooling)
9. [Async Processing and Message Queues](#9-async-processing)
10. [Rate Limiting — Protecting Your System from Overload](#10-rate-limiting)
11. [Background Jobs with Hangfire](#11-background-jobs)
12. [The Education Platform Scenario — Exam Day](#12-education-platform-scenario)
13. [Decision Guide](#13-decision-guide)
14. [Summary and Key Takeaways](#14-summary)

---

## 1. Introduction — The Day the Platform Stood Still

It's 7:55 AM on a Monday. Across Vietnam, 200,000 students are about to take their national English proficiency test using LinguaLearn. At exactly 8:00 AM, every one of them opens the app. The platform — which handles a steady 5,000 users on a normal morning — is suddenly hit with 40 times its usual traffic in under 60 seconds.

If your system is not designed for this, here is exactly what happens:

- **8:00:01** — Login requests spike. The authentication service is handling 3,000 requests per second.
- **8:00:05** — The database connection pool hits its limit. Queries start queuing.
- **8:00:12** — Application servers run out of available threads. New requests start timing out.
- **8:00:23** — Memory fills up. The garbage collector starts running frequently, pausing the application.
- **8:00:45** — Application servers start crashing. The load balancer is routing to dead instances.
- **8:01:00** — Complete outage. 200,000 students see error pages. Teachers are panicking.
- **8:01:30** — Your phone rings. It's the Minister of Education.

This scenario is not hypothetical. It has happened to real companies, often with devastating business consequences. High load engineering is the discipline of making sure it never happens to you.

This chapter teaches you how to think about load, how to find your system's weak points before they snap, and how to build systems that can absorb massive traffic spikes and keep running.

---

## 2. What Is High Load? The Numbers That Matter

"High load" is relative. A system that handles 100 requests per second might be at 10% capacity for one application and at 100% for another. To reason about load, you need to speak in concrete metrics.

### The Core Performance Metrics

#### Throughput (RPS / TPS)
The number of requests (or transactions) your system processes per second.

- **Low load:** 10-100 RPS — a small internal tool
- **Medium load:** 1,000-10,000 RPS — a mid-sized SaaS product
- **High load:** 100,000+ RPS — a nationally-used platform like LinguaLearn on exam day

#### Latency (P50, P95, P99)
How long it takes to process one request. But averages are misleading. A better measure is **percentiles**:

- **P50 (median):** 50% of requests are faster than this. If P50 = 50ms, half your users wait less than 50ms.
- **P95:** 95% of requests are faster than this. The slowest 5%.
- **P99:** 99% of requests are faster than this. The slowest 1% — your most frustrated users.
- **P99.9:** The worst 0.1% — often where you find cascading failures hiding.

```
A deceptive scenario:
  Average latency: 50ms  ← looks great!
  P95 latency:    200ms  ← getting worse
  P99 latency:   2000ms  ← 2 seconds! 1 in 100 users has a terrible experience
  P99.9 latency: 30000ms ← 30 seconds! These users are giving up

Never optimize for average. Optimize for percentiles.
```

#### Availability (The "Nines")
How often your system is actually working. Usually expressed as a percentage per year:

| Availability | Allowed Downtime / Year | Allowed Downtime / Month |
|-------------|------------------------|-------------------------|
| 99% (two nines) | 3.65 days | 7.2 hours |
| 99.9% (three nines) | 8.7 hours | 43.8 minutes |
| 99.95% | 4.4 hours | 21.9 minutes |
| 99.99% (four nines) | 52.6 minutes | 4.4 minutes |
| 99.999% (five nines) | 5.3 minutes | 26 seconds |

For LinguaLearn, where students might take exams on the platform, **99.99% availability** should be the target. That allows just 52 minutes of downtime per year.

#### Concurrency
How many requests are being processed simultaneously at any given moment. This is what creates contention for resources (database connections, threads, memory).

---

## 3. The Bottleneck Hunt — Finding What Breaks First

Every system has a **bottleneck** — the one resource that becomes exhausted first under load. Before you add more servers or optimize code, you need to find the bottleneck. Otherwise, you're treating symptoms while the real problem festers.

Think of it like a water pipe system. You can add as many wide pipes as you want, but if one section of the pipe is narrow, that narrow section determines your maximum flow rate. Widening all the other pipes doesn't help.

### The Four Common Bottlenecks

```
┌──────────────────────────────────────────────────────────────────┐
│                    Where Systems Break Under Load                  │
│                                                                    │
│  1. CPU Bottleneck                                                │
│     Symptom: CPU usage 90-100%, slow response times             │
│     Cause: Expensive computations, lack of async code            │
│     Fix: Optimize algorithms, add horizontal scaling             │
│                                                                    │
│  2. Memory Bottleneck                                             │
│     Symptom: High GC pressure, OutOfMemoryException             │
│     Cause: Large in-memory data structures, memory leaks         │
│     Fix: Streaming instead of buffering, fix leaks               │
│                                                                    │
│  3. I/O Bottleneck (Network / Disk)                              │
│     Symptom: High I/O wait, slow network transfers               │
│     Cause: Too many DB calls, large payloads, slow disks         │
│     Fix: Caching, batching, compression, faster storage          │
│                                                                    │
│  4. Database Bottleneck                                           │
│     Symptom: Slow queries, connection pool exhausted             │
│     Cause: Missing indexes, N+1 queries, too many connections    │
│     Fix: Add indexes, read replicas, query optimization          │
└──────────────────────────────────────────────────────────────────┘
```

### The N+1 Query Problem — A Hidden Load Killer

One of the most common causes of database bottlenecks is the **N+1 query problem**. It's subtle and easy to create accidentally.

```csharp
// ❌ N+1 Problem: 1 query to get all students, then 1 query PER student
// For 1,000 students, this executes 1,001 queries!
var students = await _db.Students.ToListAsync(); // 1 query

foreach (var student in students) 
{
    // EF Core lazy loads progress for each student — 1 query per student!
    var completedLessons = student.Progress.Count(p => p.IsCompleted);
    Console.WriteLine($"{student.Name}: {completedLessons} lessons completed");
}
// Total DB roundtrips: 1,001. Terrible at scale.

// ✅ Solution: Eager loading with Include()
var students = await _db.Students
    .Include(s => s.Progress.Where(p => p.IsCompleted))  // Fetched in ONE query
    .ToListAsync();
// Total DB roundtrips: 1. 1,000x better.

// ✅ Even better for large datasets: Projection with Select()
var report = await _db.Students
    .Select(s => new {
        s.Name,
        CompletedLessons = s.Progress.Count(p => p.IsCompleted)
    })
    .ToListAsync();
// Sends only needed columns, computed in the DB. Most efficient.
```

---

## 4. Scaling Strategies — Going Up vs. Going Wide

When your system runs out of capacity, you have two fundamental options:

### Vertical Scaling (Scale Up)

Make the existing machine more powerful: more CPU cores, more RAM, faster storage.

```
Before:                    After:
[2-core server]       →   [32-core server]
[8 GB RAM]            →   [256 GB RAM]
[HDD]                 →   [NVMe SSD]
```

**✅ Pros:**
- Simple — no code changes needed
- No distribution complexity
- Works for databases that are hard to shard

**❌ Cons:**
- Has a hard ceiling — the biggest machines available
- Expensive (cost grows super-linearly with specs)
- Single point of failure — one machine, one failure = total outage
- Requires downtime to upgrade

**Verdict:** Use vertical scaling first, as a quick fix. But plan for horizontal scaling as your long-term strategy.

### Horizontal Scaling (Scale Out)

Add more machines. Each machine runs the same application, and a load balancer distributes requests across them.

```
Before:                    After:
                          [Server 1]
[Single Server]    →   ┌──[Server 2]──┐
                   │   │  [Server 3]  │  Load Balancer
                   └──►│  [Server 4]  ├──► Users
                       │  [Server 5]  │
                       └─────────────┘
```

**✅ Pros:**
- Theoretically unlimited scaling — add more machines as needed
- Resilient — if one server dies, others handle the load
- Cost-efficient — use many cheap machines instead of one expensive one
- Can scale different components independently

**❌ Cons:**
- Application must be **stateless** (session data can't be stored on individual servers)
- Requires load balancing infrastructure
- Distributed system complexity (see Chapter 1)
- Database scaling is more complex

**Critical requirement for horizontal scaling:** Your application must be **stateless**. This means an HTTP request from User A to Server 1 at 10:00 AM, and then User A's next request going to Server 3 at 10:01 AM, must work identically. The application cannot store any per-user session data in the server's memory.

Instead, all shared state must live in external systems:
- User sessions → Redis
- File uploads → S3 / Azure Blob Storage  
- Distributed locks → Redis or ZooKeeper
- Caching → Redis

---

## 5. Load Balancing — Sharing the Work Fairly

A **load balancer** sits in front of your server fleet and routes incoming requests to individual servers. It's the traffic cop of your system.

### Load Balancing Algorithms

#### Round Robin
The simplest algorithm. Route request 1 to Server 1, request 2 to Server 2, request 3 to Server 3, request 4 back to Server 1...

```
Request 1 → Server 1
Request 2 → Server 2
Request 3 → Server 3
Request 4 → Server 1 (cycle repeats)
```

✅ Good for: Uniform requests where each takes roughly the same time.
❌ Bad for: Mixed workloads (short lesson fetch + slow video transcoding on the same servers).

#### Least Connections
Route each new request to the server with the fewest active connections.

```
Server 1: 150 active connections
Server 2: 23 active connections  ← Next request goes here
Server 3: 89 active connections
```

✅ Good for: Variable-length requests where some take much longer than others.
✅ More sophisticated and generally better than round robin.

#### Consistent Hashing
Route requests based on a hash of some request attribute (user ID, tenant ID). The same user always goes to the same server.

```
hash("student-123") % 3 = 1  → always Server 1
hash("student-456") % 3 = 0  → always Server 0
hash("student-789") % 3 = 2  → always Server 2
```

✅ Good for: Caching strategies where having the same server handle the same user improves cache hit rates.

### Health Checks

Load balancers continuously check if their backend servers are healthy. If a server fails its health check, the load balancer stops sending it traffic.

```csharp
// ─────────────────────────────────────────────────────────────────
// Program.cs — Health check endpoints (required for load balancers)
// ─────────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddDbContextCheck<ApplicationDbContext>("database")
    .AddRedis(builder.Configuration.GetConnectionString("Redis")!, "redis")
    .AddCheck("self", () => HealthCheckResult.Healthy());

var app = builder.Build();

// Load balancer pings this endpoint every 10 seconds
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
});

// Kubernetes liveness probe — is the app alive?
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = r => r.Name == "self"
});

// Kubernetes readiness probe — is the app ready to accept traffic?
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("ready")
});
```

---

## 6. The Caching Hierarchy — From CDN to Database

Caching is the single most effective technique for handling high load. Every layer of your system can have a cache, and together they create a **defense-in-depth** against database overload.

Think of it like a library. Instead of walking all the way to the basement archives every time you need a book (the database), you:
1. Check if you already have the book on your desk (local memory cache)
2. Check the branch library's shelf (distributed cache / Redis)
3. Check the main city library (CDN cache)
4. Only as a last resort, go to the basement archives (the database)

### The Four Cache Layers

```
Client Request
      │
      ▼
[Layer 1: CDN Cache] ─── Static assets: HTML, CSS, JS, images
      │                   Hit rate: ~95%  Latency: ~5ms
      │ Miss
      ▼
[Layer 2: API Response Cache] ─── Cached responses in Nginx / API Gateway
      │                            Hit rate: ~60%  Latency: ~10ms
      │ Miss
      ▼
[Layer 3: Distributed Cache (Redis)] ─── Shared application-level cache
      │                                   Hit rate: ~80% of DB queries  Latency: ~1ms
      │ Miss
      ▼
[Layer 4: Database] ─── Source of truth (the real data)
                          Latency: ~10-100ms. Hit rate is 100% (always has the data)
```

### CDN — Caching at the Edge

A **CDN (Content Delivery Network)** is a global network of servers that cache your content near your users. Instead of a student in Hanoi making a round trip to your server in Singapore, they get the content from the nearest CDN node — which might be in Hanoi itself, reducing latency from 30ms to 3ms.

LinguaLearn uses a CDN for:
- All static assets (JavaScript, CSS, fonts, images)
- Lesson video files (the biggest bandwidth savings)
- Cached API responses for public or rarely-changing data

### In-Memory Cache vs. Distributed Cache

```csharp
// ─────────────────────────────────────────────────────────────────
// When to use IMemoryCache (In-Process, Single Server)
// ─────────────────────────────────────────────────────────────────
// Use for: Small, frequently-read reference data that rarely changes
// Example: The list of supported languages, available lesson levels
// ─────────────────────────────────────────────────────────────────

builder.Services.AddMemoryCache();

public class ReferenceDataService
{
    private readonly IMemoryCache _cache;
    private readonly ILanguageRepository _repository;

    public async Task<IEnumerable<Language>> GetSupportedLanguagesAsync()
    {
        // Cache key for supported languages
        return await _cache.GetOrCreateAsync("supported_languages", async entry =>
        {
            // This rarely changes — cache for 6 hours
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6);
            entry.Priority = CacheItemPriority.High; // Don't evict this early
            
            return await _repository.GetAllLanguagesAsync();
        }) ?? Enumerable.Empty<Language>();
    }
}
```

```csharp
// ─────────────────────────────────────────────────────────────────
// When to use IDistributedCache (Redis, Multi-Server)
// ─────────────────────────────────────────────────────────────────
// Use for: User-specific data, tenant-specific data, session state
// Example: Lesson content (changes occasionally), student progress snapshots
// ─────────────────────────────────────────────────────────────────

// See Chapter 1, Section 8 for the full Redis caching implementation.
// The pattern is:
//   1. Get from Redis
//   2. If miss: get from DB, store in Redis with TTL
//   3. On update: delete from Redis (cache invalidation)
```

### Cache TTL Strategy

Choosing the right Time-To-Live (TTL) for cached data is an art:

| Data Type | Suggested TTL | Reasoning |
|-----------|--------------|-----------|
| Lesson content | 1 hour | Teachers update lessons occasionally |
| Video metadata | 4 hours | Rarely changes |
| Student profile | 15 minutes | User might update their profile |
| Student progress | 5 minutes | Updates frequently during study sessions |
| Tenant configuration | 30 minutes | Admins change settings occasionally |
| Supported languages | 6 hours | Extremely stable |
| Authentication tokens | Match JWT expiry | Must be in sync |

---

## 7. Database at Scale — Read Replicas and Sharding

When caching isn't enough and the database itself becomes the bottleneck, there are two main strategies.

### Read Replicas

In most applications, reads (SELECT) vastly outnumber writes (INSERT/UPDATE/DELETE). Read replicas take advantage of this by maintaining read-only copies of the database that can serve SELECT queries.

```
                Write Operations (20% of traffic)
                        │
                        ▼
           ┌─────────────────────────┐
           │      Primary DB         │
           │   (Read + Write)        │
           └─────────────────────────┘
                  │           │
        Async Replication  Async Replication
                  │           │
    ┌─────────────┴──┐   ┌───┴───────────────┐
    │   Read Replica 1│   │  Read Replica 2   │
    │   (Read-Only)   │   │  (Read-Only)      │
    └─────────────────┘   └───────────────────┘
           ▲                       ▲
    Read Operations (80%)   Read Operations (80%)
```

```csharp
// ─────────────────────────────────────────────────────────────────
// Program.cs — Registering Read Replica DbContext
// ─────────────────────────────────────────────────────────────────
// Pattern: Use the primary DB for writes, read replicas for reads.
// This is called CQRS at the infrastructure level.
// ─────────────────────────────────────────────────────────────────

// Write context — points to the primary database
builder.Services.AddDbContext<WriteDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("PrimaryDatabase")));

// Read context — points to a read replica
// Can point to a load balancer that routes across multiple replicas
builder.Services.AddDbContext<ReadDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("ReadReplicaDatabase")));
```

```csharp
// ─────────────────────────────────────────────────────────────────
// LessonRepository.cs — Using read/write split
// ─────────────────────────────────────────────────────────────────
public class LessonRepository : ILessonRepository
{
    private readonly WriteDbContext _writeDb;  // For INSERT/UPDATE/DELETE
    private readonly ReadDbContext _readDb;    // For SELECT queries

    public LessonRepository(WriteDbContext writeDb, ReadDbContext readDb)
    {
        _writeDb = writeDb;
        _readDb = readDb;
    }

    // Read operations → Read Replica (potentially hundreds of these per second)
    public async Task<Lesson?> GetByIdAsync(int lessonId)
        => await _readDb.Lessons.FindAsync(lessonId);

    public async Task<List<Lesson>> GetByUnitAsync(int unit)
        => await _readDb.Lessons
            .Where(l => l.Unit == unit)
            .OrderBy(l => l.Id)
            .ToListAsync();

    // Write operations → Primary DB (much less frequent)
    public async Task<Lesson> CreateAsync(Lesson lesson)
    {
        _writeDb.Lessons.Add(lesson);
        await _writeDb.SaveChangesAsync();
        return lesson;
    }

    public async Task UpdateAsync(Lesson lesson)
    {
        _writeDb.Lessons.Update(lesson);
        await _writeDb.SaveChangesAsync();
    }
}
```

> **⚠️ Replication Lag:** Read replicas are updated asynchronously, so they might be 10-200ms behind the primary. If a teacher updates a lesson and then immediately refreshes their page, they might briefly see the old version. This is eventual consistency in action. For most education content, this is acceptable. For financial data (like payment records), always read from the primary.

### Database Sharding

Sharding splits data across multiple databases by a **shard key**. Each database (shard) holds a subset of the data.

```
                     Incoming Query
                           │
                     Shard Router
                     (route by TenantId)
                     /     │     \
          ┌─────────┘      │      └──────────┐
          ▼                ▼                 ▼
   Shard 0             Shard 1           Shard 2
   Tenants A-H         Tenants I-Q       Tenants R-Z
   (DB server 1)       (DB server 2)     (DB server 3)
```

Sharding is complex and should be a last resort. Consider it only when:
- Your single database server is running at maximum capacity even with read replicas
- Your dataset is too large to fit on any single machine
- You need to exceed ~10,000 write operations per second

For LinguaLearn, the **TenantId** is a natural shard key — all data for School Tokyo goes to Shard 0, all data for School London goes to Shard 1, etc.

---

## 8. Connection Pooling — Don't Waste Your Database Connections

Creating a new database connection is expensive — it involves network handshaking, authentication, and resource allocation on both the app and database side. This can take 50-500ms. In a high-load scenario, creating a new connection for every request is catastrophic.

**Connection pooling** maintains a pool of pre-established connections that requests can borrow and return, instead of creating and destroying connections for each request.

```
Without Connection Pool:
  Request 1 → [Create Connection] → Query → [Destroy Connection]  (~100ms overhead)
  Request 2 → [Create Connection] → Query → [Destroy Connection]  (~100ms overhead)
  
With Connection Pool:
  App Start → [Create 20 Connections] → Pool
  
  Request 1 → [Borrow Connection from Pool] → Query → [Return to Pool]  (~1ms overhead)
  Request 2 → [Borrow Connection from Pool] → Query → [Return to Pool]  (~1ms overhead)
  Request 3 → Pool has available connection → instant borrow
```

```csharp
// ─────────────────────────────────────────────────────────────────
// appsettings.json — Connection string with pooling parameters
// ─────────────────────────────────────────────────────────────────
// PostgreSQL connection string (via Npgsql) with explicit pool settings
// {
//   "ConnectionStrings": {
//     "PrimaryDatabase": "Host=db-primary.internal;Database=lingualearn;
//                         Username=app_user;Password=secret;
//                         Minimum Pool Size=5;Maximum Pool Size=100;
//                         Connection Idle Lifetime=300;
//                         Connection Pruning Interval=10"
//   }
// }

// ─────────────────────────────────────────────────────────────────
// What each pool parameter means:
// ─────────────────────────────────────────────────────────────────
// Minimum Pool Size=5     → Keep at least 5 connections alive at all times.
//                           They'll be ready for the first burst of traffic.
//
// Maximum Pool Size=100   → Never exceed 100 connections to this DB server.
//                           PostgreSQL's limit is typically 100-1000 connections.
//                           Each connection uses ~5MB of RAM on the DB server.
//                           With 5 app servers × 100 connections = 500 total.
//
// Connection Idle Lifetime=300 → Close connections idle for 5 minutes.
//                                Prevents hoarding connections we're not using.
//
// Key insight: If all 100 connections are busy and a new request comes in,
// it will WAIT for a connection to become available.
// If it waits too long → Timeout. This is why at extreme load,
// even a good connection pool can become the bottleneck.
// ─────────────────────────────────────────────────────────────────
```

---

## 9. Async Processing and Message Queues

Not everything needs to happen immediately as part of the HTTP request. Expensive or time-consuming operations can be **deferred** to a background process.

### Synchronous vs. Asynchronous Processing

```
Synchronous (everything in the request):
─────────────────────────────────────────
Student submits quiz
         │
         ▼ [10ms] Save quiz answers to DB
         ▼ [50ms] Calculate score
         ▼ [30ms] Update student progress
         ▼ [200ms] Generate PDF certificate (if passed)
         ▼ [150ms] Send congratulations email
         ▼ [80ms] Update leaderboard
Total: 520ms before student sees result. Slow and fragile.

Asynchronous (fast path + background processing):
──────────────────────────────────────────────────
Student submits quiz
         │
         ▼ [10ms] Save quiz answers to DB
         ▼ [50ms] Calculate score
         ▼ [5ms]  Publish "QuizCompleted" event to message queue
         ▼ Response returned to student: "Quiz completed! Score: 87%"
Total: 65ms. Fast and resilient.

Meanwhile, in the background:
Event "QuizCompleted" is processed by:
  ├── CertificateService: generates PDF certificate
  ├── EmailService: sends congratulations email
  └── LeaderboardService: updates rankings
These run in parallel and don't block the student's response.
```

### Message Queue with RabbitMQ and MassTransit

```csharp
// Install: dotnet add package MassTransit.RabbitMQ

// ─────────────────────────────────────────────────────────────────
// Events/QuizCompletedEvent.cs — The message that gets published
// ─────────────────────────────────────────────────────────────────
public record QuizCompletedEvent
{
    public int StudentId { get; init; }
    public string TenantId { get; init; } = string.Empty;
    public int LessonId { get; init; }
    public int ScorePercent { get; init; }
    public bool Passed { get; init; }
    public DateTime CompletedAt { get; init; }
}
```

```csharp
// ─────────────────────────────────────────────────────────────────
// Program.cs — Configure MassTransit with RabbitMQ
// ─────────────────────────────────────────────────────────────────
builder.Services.AddMassTransit(mt =>
{
    // Register consumers (background event handlers)
    mt.AddConsumer<SendCongratulationsEmailConsumer>();
    mt.AddConsumer<GenerateCertificateConsumer>();
    mt.AddConsumer<UpdateLeaderboardConsumer>();

    mt.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host("rabbitmq://rabbitmq.lingualearn.internal", h =>
        {
            h.Username("lingualearn_app");
            h.Password("secret");
        });

        // Configure a receive endpoint for the QuizCompleted event
        cfg.ReceiveEndpoint("quiz-completed-events", e =>
        {
            e.ConfigureConsumer<SendCongratulationsEmailConsumer>(context);
            e.ConfigureConsumer<GenerateCertificateConsumer>(context);
            e.ConfigureConsumer<UpdateLeaderboardConsumer>(context);
        });
    });
});
```

```csharp
// ─────────────────────────────────────────────────────────────────
// QuizController.cs — Publishing the event (the fast path)
// ─────────────────────────────────────────────────────────────────
[ApiController]
[Route("api/quiz")]
public class QuizController : ControllerBase
{
    private readonly IQuizService _quizService;
    private readonly IPublishEndpoint _publishEndpoint;  // MassTransit publisher

    public QuizController(IQuizService quizService, IPublishEndpoint publishEndpoint)
    {
        _quizService = quizService;
        _publishEndpoint = publishEndpoint;
    }

    [HttpPost("{lessonId:int}/submit")]
    public async Task<ActionResult<QuizResult>> SubmitQuiz(int lessonId, SubmitQuizRequest request)
    {
        // Fast path: save answers and calculate score (synchronous)
        var result = await _quizService.SubmitAndScoreAsync(lessonId, request);

        // Publish event to the message queue (fast - just puts a message in a queue)
        await _publishEndpoint.Publish(new QuizCompletedEvent
        {
            StudentId = result.StudentId,
            TenantId = _tenantContext.TenantId,
            LessonId = lessonId,
            ScorePercent = result.ScorePercent,
            Passed = result.Passed,
            CompletedAt = DateTime.UtcNow
        });

        // Return result immediately — don't wait for emails, certificates, etc.
        return Ok(result);
    }
}
```

```csharp
// ─────────────────────────────────────────────────────────────────
// Consumers/SendCongratulationsEmailConsumer.cs
// This runs in the background, after the HTTP response is already sent
// ─────────────────────────────────────────────────────────────────
public class SendCongratulationsEmailConsumer : IConsumer<QuizCompletedEvent>
{
    private readonly IEmailService _emailService;
    private readonly IStudentRepository _students;
    private readonly ILogger<SendCongratulationsEmailConsumer> _logger;

    public SendCongratulationsEmailConsumer(
        IEmailService emailService,
        IStudentRepository students,
        ILogger<SendCongratulationsEmailConsumer> logger)
    {
        _emailService = emailService;
        _students = students;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<QuizCompletedEvent> context)
    {
        var @event = context.Message;
        if (!@event.Passed) return; // Only send email if they passed

        var student = await _students.GetByIdAsync(@event.StudentId);
        if (student is null) return;

        _logger.LogInformation("Sending congratulations email to {StudentName} for Lesson {LessonId}",
            student.Name, @event.LessonId);

        await _emailService.SendCongratulationsAsync(
            toEmail: student.Email,
            studentName: student.Name,
            lessonId: @event.LessonId,
            score: @event.ScorePercent
        );
    }
}
```

---

## 10. Rate Limiting — Protecting Your System from Overload

Even with all the caching and scaling in the world, you need a **rate limiter** — a mechanism that limits how many requests a single client (user, IP, or tenant) can make in a given time window. This protects your system from:

- A buggy client making thousands of requests per second (accidental DoS)
- A student refreshing a page 100 times because they're anxious
- A school running a bot to scrape all lesson content
- A malicious actor trying to overwhelm your API

```csharp
// Install: Included in .NET 7+ via Microsoft.AspNetCore.RateLimiting

// ─────────────────────────────────────────────────────────────────
// Program.cs — Configuring Rate Limiting
// ─────────────────────────────────────────────────────────────────
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Global rate limit: No single client can exceed 100 req/s across the whole API
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        // Partition by authenticated user ID (or IP for anonymous requests)
        var userId = context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                     ?? context.Connection.RemoteIpAddress?.ToString()
                     ?? "anonymous";

        return RateLimitPartition.GetFixedWindowLimiter(userId, _ =>
            new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,               // Max 100 requests...
                Window = TimeSpan.FromSeconds(1), // ...per 1 second
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 5  // Allow 5 requests to queue before rejecting
            });
    });

    // Specific limit for the quiz submission endpoint (prevent rapid resubmissions)
    options.AddFixedWindowLimiter("quiz_submission", limiterOptions =>
    {
        limiterOptions.PermitLimit = 5;             // Max 5 quiz submissions...
        limiterOptions.Window = TimeSpan.FromMinutes(1); // ...per minute
    });

    // Specific limit for video streaming (prevent bandwidth abuse)
    options.AddTokenBucketLimiter("video_stream", limiterOptions =>
    {
        limiterOptions.TokenLimit = 10;           // Max 10 stream starts
        limiterOptions.ReplenishmentPeriod = TimeSpan.FromMinutes(1);
        limiterOptions.TokensPerPeriod = 3;       // Refill 3 tokens per minute
        limiterOptions.AutoReplenishment = true;
    });
});

// Register middleware
var app = builder.Build();
app.UseRateLimiter();
```

```csharp
// Apply rate limiting to specific endpoints
[HttpPost("{lessonId:int}/submit")]
[EnableRateLimiting("quiz_submission")]  // Apply the quiz-specific rate limit
public async Task<IActionResult> SubmitQuiz(int lessonId, SubmitQuizRequest request)
{
    // ...
}
```

---

## 11. Background Jobs with Hangfire

Some tasks need to run on a schedule (e.g., generate weekly progress reports) or be queued for reliable, persistent background processing. **Hangfire** is the most popular library for this in .NET.

Unlike the fire-and-forget message queue approach, Hangfire stores jobs in a database. If the server crashes while processing a job, the job will be retried when the server comes back up.

```csharp
// Install: dotnet add package Hangfire.AspNetCore
// Install: dotnet add package Hangfire.PostgreSql (or SqlServer)

// ─────────────────────────────────────────────────────────────────
// Program.cs — Hangfire setup
// ─────────────────────────────────────────────────────────────────
builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(
        builder.Configuration.GetConnectionString("HangfireDatabase")));

builder.Services.AddHangfireServer(options =>
{
    options.WorkerCount = 5; // 5 background worker threads
    options.Queues = new[] { "critical", "default", "low" }; // Priority queues
});

var app = builder.Build();

// Optional: Hangfire Dashboard (password-protect this!)
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new HangfireAdminAuthorizationFilter() }
});

// ─────────────────────────────────────────────────────────────────
// Schedule recurring jobs at startup
// ─────────────────────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var recurringJobManager = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
    
    // Generate weekly progress reports every Sunday at 2 AM UTC
    recurringJobManager.AddOrUpdate<WeeklyReportJob>(
        "weekly-progress-reports",
        job => job.GenerateForAllTenantsAsync(),
        Cron.Weekly(DayOfWeek.Sunday, hour: 2)
    );
    
    // Clean up expired session tokens daily at 3 AM UTC
    recurringJobManager.AddOrUpdate<TokenCleanupJob>(
        "cleanup-expired-tokens",
        job => job.CleanupAsync(),
        Cron.Daily(hour: 3)
    );
}
```

```csharp
// ─────────────────────────────────────────────────────────────────
// Jobs/WeeklyReportJob.cs
// ─────────────────────────────────────────────────────────────────
public class WeeklyReportJob
{
    private readonly ITenantRepository _tenantRepo;
    private readonly IProgressReportService _reportService;
    private readonly IEmailService _emailService;
    private readonly ILogger<WeeklyReportJob> _logger;

    public WeeklyReportJob(/* ... inject services ... */) { /* ... */ }

    // This method is stored in Hangfire's DB and executed in the background.
    // If it fails, Hangfire retries it automatically (up to 10 times by default).
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 60, 300, 900 })]
    public async Task GenerateForAllTenantsAsync()
    {
        var tenants = await _tenantRepo.GetAllActiveTenantsAsync();
        _logger.LogInformation("Generating weekly reports for {TenantCount} tenants", tenants.Count);

        // Process tenants in parallel but limit concurrency to avoid DB overload
        var semaphore = new SemaphoreSlim(10); // Max 10 concurrent report generations
        var tasks = tenants.Select(async tenant =>
        {
            await semaphore.WaitAsync();
            try
            {
                var report = await _reportService.GenerateWeeklyReportAsync(tenant.TenantId);
                await _emailService.SendWeeklyReportAsync(tenant.AdminEmail, report);
                _logger.LogInformation("Weekly report sent for tenant {TenantId}", tenant.TenantId);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
    }
}
```

---

## 12. The Education Platform Scenario — Exam Day

Let's trace LinguaLearn's exam day scenario with all high-load patterns applied:

```
08:00:00 — 200,000 students simultaneously open the exam platform.

LOAD BALANCER:
- Round-robin distributes requests across 20 application server instances
- Health checks confirm all 20 servers are alive
- Auto-scaling policy detects CPU > 70% → starts 10 more instances

CDN LAYER (handles ~60% of requests):
- Login page HTML/CSS/JS → served from CDN (Singapore/Jakarta/HCM City nodes)
- Latency: 5-10ms. No application server needed.

RATE LIMITER:
- Detects unusual spike → activates stricter per-IP rate limiting
- Buggy student client making 500 req/s → blocked automatically (429)

LESSON LOADING (40% of requests reach app servers):
- Each student loads their exam lesson page
- Redis cache: 85% hit rate → lesson content served in ~2ms
- 15% cache miss → DB read replica query → ~30ms → stored in Redis

QUIZ SUBMISSIONS (starting at 08:15):
- Students submit quiz answers → synchronous: save + score (~15ms)
- Async: "QuizCompleted" event published to RabbitMQ
  - Email Service processes email queue (non-urgent, can lag)
  - Certificate Service processes certificate queue (non-urgent)
  - These are isolated from the fast path — even if they're slow, 
    students still get their scores instantly

DATABASE:
- Primary DB: receives only WRITE traffic (quiz submissions)
- Read Replica 1 & 2: handle all READ traffic for lesson fetching
- Connection pool: 100 connections per app server × 20 servers = 2,000 total
  DB can handle this easily (PostgreSQL max_connections = 5,000)

08:30:00 — Traffic starts subsiding as students finish.
- Auto-scaler detects CPU < 30% → starts terminating extra instances
- System returns to baseline without manual intervention

RESULT: 200,000 students served. Average latency: 52ms. Zero downtime.
```

---

## 13. Decision Guide

| Technique | When to Apply | Cost |
|-----------|--------------|------|
| Vertical scaling | First response to capacity issues | Medium (hardware) |
| Horizontal scaling | When vertical hits ceiling | Medium (infra + code changes) |
| CDN | Always, from day 1 | Low |
| In-memory cache | For stable reference data | Very low |
| Redis distributed cache | When you have multiple servers | Low |
| Read replicas | When DB reads are the bottleneck | Medium |
| Sharding | Extreme scale (10,000+ writes/sec) | Very high |
| Rate limiting | Always, to protect the system | Very low |
| Message queues | For non-critical async operations | Low |
| Background jobs | For scheduled/delayed work | Low |
| Connection pooling | Always, built into most ORMs | Very low |

---

## 14. Summary and Key Takeaways

### Core Concepts

| Concept | One-Line Summary |
|---------|-----------------|
| Throughput (RPS) | How many requests the system processes per second |
| Latency (P99) | How fast the slowest 1% of requests are served |
| Availability (9s) | What percentage of time the system is operational |
| Vertical scaling | Make one machine more powerful |
| Horizontal scaling | Add more machines |
| Load balancer | Routes traffic fairly across multiple servers |
| CDN | Cache static content near users |
| Redis | Distributed cache shared across all servers |
| Read replica | Read-only database copy for scaling SELECT queries |
| Sharding | Split data across multiple databases |
| Rate limiter | Prevent any single client from overwhelming the system |
| Message queue | Decouple expensive work from the fast HTTP request path |
| Hangfire | Persistent background job scheduler |

### The Golden Rules of High Load Systems

1. **Measure before you optimize** — Never guess where the bottleneck is. Use profiling tools.
2. **Cache at every layer** — CDN → Redis → DB query cache. Each layer dramatically multiplies your capacity.
3. **Make your application stateless** — Only stateless apps can be horizontally scaled.
4. **Decouple with message queues** — Don't let slow background work slow down user-facing requests.
5. **Rate limit everything** — A single misbehaving client should never take down the system.

### What's Next

You now know how to make a system handle massive load without collapsing. In the next chapter, we look at how to organize a large team and large codebase using microservices — so different teams can scale, deploy, and maintain their parts of the platform independently.

*→ Continue to: [Chapter 4 — Microservices](./book_ch4_microservices.md)*

---

*Chapter 3 Complete · 14 sections · High Load Systems*
