# Chapter 3: High Load Systems

> **Performance Engineering · AWS Auto-Scaling · High Availability**
> *"Any system popular enough to matter will eventually be crushed under its own success. The engineers who survive this know it's coming and prepare."*

---

## Table of Contents

1. [Introduction — National Exam Day](#1-introduction)
2. [What Is High Load? The Numbers That Matter](#2-what-is-high-load)
3. [The Bottleneck Hunt — Finding What Breaks First](#3-bottleneck-hunt)
4. [Scaling on AWS — Vertical, Horizontal, and Auto-Scaling](#4-scaling-on-aws)
5. [AWS Application Load Balancer](#5-application-load-balancer)
6. [The Caching Hierarchy — CloudFront to ElastiCache to RDS](#6-caching-hierarchy)
7. [MediatR Caching Pipeline Behavior](#7-mediatr-caching-behavior)
8. [RDS at Scale — Read Replicas and Connection Pooling](#8-rds-at-scale)
9. [Async Processing with Amazon SQS](#9-async-processing-with-sqs)
10. [Rate Limiting — Protecting Grapeseed from Overload](#10-rate-limiting)
11. [Background Jobs with Hangfire on AWS](#11-background-jobs)
12. [The Grapeseed Scenario — Exam Day](#12-grapeseed-scenario)
13. [Decision Guide](#13-decision-guide)
14. [Summary and Key Takeaways](#14-summary)

---

## 1. Introduction — National Exam Day

It's Sunday evening in Vietnam. Tomorrow is the national English proficiency assessment for 180,000 middle school students. As part of their preparation, all students are using the Grapeseed program assigned by their school district. At 8:00 AM Monday, every single one of them opens the app.

In 60 seconds, Grapeseed goes from its normal Monday morning traffic of 8,000 active users to 180,000. That's a 22x traffic spike. Your AWS bill is about to have a very interesting line item — or your error logs are.

If your infrastructure isn't designed for this moment:

- **8:00:00** — ECS tasks hit 100% CPU. New requests start queuing at the ALB.
- **8:00:15** — RDS connection pool exhausted. EF Core throws `NpgsqlException: connection pool exhausted`.
- **8:00:30** — ElastiCache cache misses spike because the app is restarting.
- **8:00:45** — ECS health checks fail. ALB routes to fewer and fewer healthy tasks.
- **8:01:00** — Complete outage. 180,000 students see a white error screen.
- **8:01:30** — The school district administrator calls Grapeseed support.

**High load engineering prevents this.** It is the discipline of anticipating traffic patterns, designing systems with appropriate headroom and elasticity, and building automatic responses to demand spikes — so that when exam day arrives, the platform absorbs it without anyone touching a server.

---

## 2. What Is High Load? The Numbers That Matter

"High load" is relative. To reason about it, you need concrete metrics.

### Throughput (Requests Per Second)

How many requests your system processes every second:
- **Normal morning:** ~500 RPS (teachers planning lessons, students doing homework)
- **After-school peak:** ~3,000 RPS (students doing assigned lessons)
- **Exam prep day:** ~15,000 RPS (entire student body active simultaneously)

### Latency Percentiles (P50, P95, P99)

Average latency is misleading. Use percentiles:

| Metric | Meaning | Grapeseed Target |
|--------|---------|-----------------|
| **P50 (median)** | Half of requests are faster than this | < 100ms |
| **P95** | 95% of requests are faster than this | < 300ms |
| **P99** | 99% of requests are faster than this | < 1,000ms |
| **P99.9** | The slowest 0.1% | < 3,000ms |

A system that shows "average: 80ms" but "P99: 8,000ms" is not performing well — 1 in 100 students is waiting 8 seconds.

### Availability Targets

| Availability | Downtime / Year | Downtime / Month | Target |
|-------------|-----------------|-----------------|--------|
| 99% | 3.65 days | 7.2 hours | ❌ Too low for Grapeseed |
| 99.9% | 8.7 hours | 43.8 minutes | ⚠️ Minimum |
| 99.95% | 4.4 hours | 21.9 minutes | ✅ Good for Grapeseed |
| 99.99% | 52.6 minutes | 4.4 minutes | 🎯 Target for exam platform |

### Concurrency

How many requests are being processed simultaneously. This is what creates contention for shared resources like database connections and ElastiCache connections.

---

## 3. The Bottleneck Hunt — Finding What Breaks First

Every system has a **bottleneck** — the one resource that becomes exhausted first under load. Before you add more servers, find the bottleneck. Scaling the wrong layer is wasted money and effort.

```
┌─────────────────────────────────────────────────────────────────┐
│              Common Grapeseed Bottlenecks                        │
│                                                                   │
│  1. ECS Task CPU Overload                                        │
│     Symptom: CloudWatch CPU metric > 90%, slow responses        │
│     Cause: Expensive EF Core queries, missing async/await       │
│     Fix: Add ECS tasks (Auto Scaling), optimize queries         │
│                                                                   │
│  2. RDS Connection Pool Exhausted                                │
│     Symptom: NpgsqlException pool errors in CloudWatch logs     │
│     Cause: Too many concurrent ECS tasks, too few connections   │
│     Fix: Add ElastiCache caching, increase pool size,           │
│            add RDS Proxy, add read replicas                     │
│                                                                   │
│  3. ElastiCache Memory Full                                      │
│     Symptom: Cache eviction rate spikes, hit rate drops         │
│     Cause: Caching too much data without TTL management         │
│     Fix: Review TTLs, increase node size, tune eviction policy  │
│                                                                   │
│  4. RDS CPU / I/O Bound Queries                                  │
│     Symptom: RDS CPU > 80%, slow query logs fill up             │
│     Cause: Missing indexes, N+1 queries, CartesianExplosion     │
│     Fix: Add indexes, optimize EF Core LINQ, add read replicas  │
└─────────────────────────────────────────────────────────────────┘
```

### The N+1 Problem — A Silent Grapeseed Killer

The N+1 query problem is one of the most common performance killers in EF Core applications. It occurs when you load a list of entities and then access a related entity for each one — triggering N additional database queries.

```csharp
// ❌ N+1 PROBLEM: 1 query to get students, then 1 per student for progress
// For 200 students → 201 queries executed!
var students = await _db.Students.ToListAsync();
foreach (var student in students)
{
    // EF Core lazy-loads progress on each access → separate SQL per student!
    var lastLesson = student.Progress.MaxBy(p => p.CompletedAt);
    Console.WriteLine($"{student.Name}: Last lesson unit {lastLesson?.Unit}");
}
// Total: 201 round-trips to RDS. Catastrophic at scale.

// ✅ SOLUTION A: Eager loading with Include()
var students = await _db.Students
    .Include(s => s.Progress)        // JOIN in a single query
    .ToListAsync();
// Total: 1 RDS query. 200x improvement.

// ✅ SOLUTION B: Projection with Select() — even more efficient
// Only fetches the columns you actually need, computes aggregates in DB
var studentSummaries = await _db.Students
    .Select(s => new StudentSummaryDto
    {
        StudentId = s.Id,
        Name = s.Name,
        CurrentUnit = s.CurrentUnit,
        CompletedLessons = s.Progress.Count(p => p.IsCompleted),
        LastCompletedAt = s.Progress
            .Where(p => p.IsCompleted)
            .Max(p => (DateTime?)p.CompletedAt)
    })
    .ToListAsync();
// Total: 1 query, returning only needed columns. Most efficient.
```

### Using MediatR for Consistent Query Patterns

Because all queries go through MediatR handlers, you can enforce good patterns once:

```csharp
// A well-structured MediatR query handler — no N+1 possible
public class GetClassProgressQueryHandler
    : IRequestHandler<GetClassProgressQuery, ClassProgressResponse>
{
    private readonly GrapeseekDbContext _db;

    public async Task<ClassProgressResponse> Handle(
        GetClassProgressQuery request,
        CancellationToken ct)
    {
        // Single, efficient query with projection
        // The GlobalQueryFilter automatically adds WHERE SchoolId = @schoolId
        var studentData = await _db.Students
            .Where(s => s.CurrentUnit == request.Unit)
            .Select(s => new StudentProgressItem
            {
                StudentId = s.Id,
                Name = s.Name,
                CompletedLessons = s.Progress.Count(p => p.IsCompleted && p.Unit == request.Unit),
                AverageScore = s.Progress
                    .Where(p => p.IsCompleted && p.Unit == request.Unit && p.ScorePercent > 0)
                    .Average(p => (double?)p.ScorePercent) ?? 0
            })
            .OrderBy(s => s.Name)
            .ToListAsync(ct);

        return new ClassProgressResponse
        {
            Unit = request.Unit,
            Students = studentData,
            ClassAverageScore = studentData.Any()
                ? studentData.Average(s => s.AverageScore) 
                : 0
        };
    }
}
```

---

## 4. Scaling on AWS — Vertical, Horizontal, and Auto-Scaling

### Vertical Scaling (Scale Up)

Upgrade the existing resource to a more powerful tier:
- ECS task: increase vCPU from 0.5 to 2 vCPU
- RDS: upgrade from `db.t3.medium` to `db.r6g.xlarge`

**✅ Good for:** Quick fix, database scaling (harder to shard).
**❌ Bad for:** Long-term — has a hard ceiling and creates a single point of failure.

### Horizontal Scaling (Scale Out)

Add more instances of the same resource:
- ECS: increase desired task count from 3 to 20
- RDS: add read replicas

**✅ Good for:** Application tier — stateless ECS tasks scale horizontally perfectly.
**❌ Requires:** Application must be **stateless**. No session data in ECS task memory — use ElastiCache for sessions.

### ECS Fargate Auto Scaling

The best of both worlds for ECS: **automatically add or remove tasks** based on real-time metrics.

```json
// ECS Auto Scaling Policy (Terraform or CloudFormation)
{
  "auto_scaling": {
    "min_capacity": 3,      // Always keep at least 3 tasks running (for HA)
    "max_capacity": 50,     // Never exceed 50 tasks (cost control)
    "target_tracking_policies": [
      {
        "name": "cpu-target-tracking",
        "metric": "ECSServiceAverageCPUUtilization",
        "target_value": 65,           // Scale out when average CPU > 65%
        "scale_out_cooldown": 60,     // Wait 60s after scaling out before scaling again
        "scale_in_cooldown": 300      // Wait 5 minutes before scaling in (avoid thrashing)
      },
      {
        "name": "memory-target-tracking",
        "metric": "ECSServiceAverageMemoryUtilization",
        "target_value": 75,
        "scale_out_cooldown": 60,
        "scale_in_cooldown": 300
      }
    ]
  }
}
```

With this configuration, when exam day traffic hits:
1. ECS detects average CPU climbing above 65%
2. ECS Auto Scaling adds tasks (roughly 2x tasks every 60 seconds until stable)
3. New tasks register with the ALB automatically
4. ALB distributes traffic to the growing task fleet
5. After exam day, CPU drops — ECS scales back in, saving money

**For Grapeseed, set up a Scheduled Scaling action** for known exam days:

```json
{
  "scheduled_actions": [
    {
      "name": "exam-day-scale-up",
      "schedule": "cron(0 0 * * MON)",    // Every Monday at midnight UTC
      "min_capacity": 15,                  // Pre-warm 15 tasks before traffic hits
      "max_capacity": 80
    },
    {
      "name": "exam-day-scale-down",
      "schedule": "cron(0 12 * * MON)",   // Scale back at noon UTC
      "min_capacity": 3,
      "max_capacity": 50
    }
  ]
}
```

Pre-warming eliminates the cold-start lag where tasks need 30-60 seconds to spin up before accepting traffic.

---

## 5. AWS Application Load Balancer

The ALB is Grapeseed's traffic cop. It sits between CloudFront and the ECS tasks and routes each request to a healthy, available task.

### ALB Health Checks

The ALB pings every ECS task's `/health` endpoint every 15 seconds. If a task fails 2 consecutive health checks, it's marked as unhealthy and no new requests are routed to it.

```csharp
// ─────────────────────────────────────────────────────────────────
// Health check endpoint — required for ECS + ALB
// ─────────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    // Check RDS PostgreSQL connectivity
    .AddNpgSql(
        connectionString: builder.Configuration.GetConnectionString("GrapeseekDb")!,
        name: "rds-postgres",
        tags: new[] { "db", "ready" })
    // Check SQL Server (Analytics DB) connectivity
    .AddSqlServer(
        connectionString: builder.Configuration.GetConnectionString("AnalyticsDb")!,
        name: "rds-sqlserver",
        tags: new[] { "db", "ready" })
    // Check ElastiCache connectivity
    .AddRedis(
        builder.Configuration["AWS:ElastiCache:Endpoint"]!,
        name: "elasticache",
        tags: new[] { "cache", "ready" })
    // Basic self-check — just confirms the process is alive
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "live" });

var app = builder.Build();

// ALB health check — checks all registered health checks
// Returns 200 if healthy, 503 if any check fails
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse,
    ResultStatusCodes =
    {
        [HealthStatus.Healthy] = StatusCodes.Status200OK,
        [HealthStatus.Degraded] = StatusCodes.Status200OK,  // Degraded still accepts traffic
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
    }
});

// ECS liveness probe — just "is the process alive?"
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("live")
});

// ECS readiness probe — "is the task ready to serve traffic?"
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("ready")
});
```

---

## 6. The Caching Hierarchy — CloudFront to ElastiCache to RDS

Think of Grapeseed's caching as four progressively slower layers:

```
Student Request
      │
      ▼
[Layer 1: CloudFront CDN]
  - Static assets: HTML, CSS, JavaScript, fonts, lesson images
  - Lesson video streaming (S3 → CloudFront → Student)
  - Cache duration: 24 hours for assets, 1 hour for API responses on public endpoints
  - Hit rate: ~70% of all traffic
  - Latency: 5-20ms from nearest edge location
      │
      │ Cache miss for dynamic API calls
      ▼
[Layer 2: AWS API Gateway Cache]
  - Caches responses from ECS tasks for public/shared endpoints
  - Example: GET /api/grapeseed-units (list of all units — same for everyone)
  - Cache duration: 5 minutes
  - Latency: ~10ms if cached
      │
      │ Authenticated / tenant-specific requests
      ▼
[Layer 3: Amazon ElastiCache (Redis)]
  - Lesson content, school configuration, student profiles
  - Managed by application code (Cache-Aside pattern)
  - Cache duration: varies by data type (see table below)
  - Latency: ~1ms
      │
      │ Cache miss
      ▼
[Layer 4: Amazon RDS (PostgreSQL / SQL Server)]
  - Source of truth for all data
  - Latency: 10-50ms for indexed queries
  - Always has the data; question is how often to hit it
```

### Cache TTL Strategy for Grapeseed

| Data Type | TTL | Reasoning |
|-----------|-----|-----------|
| Grapeseed unit/lesson metadata | 4 hours | Content doesn't change without a curriculum update |
| School (tenant) configuration | 30 minutes | Settings change occasionally |
| Student profile | 15 minutes | Students update names/emails occasionally |
| Student progress snapshot | 5 minutes | Updated frequently during study sessions |
| Teacher class list | 10 minutes | Class assignments change at semester start |
| Feature flags for school | 30 minutes | Subscription tier changes are rare |
| Authentication token data | Match JWT expiry | Must be in sync with identity service |

### CloudFront Configuration for Grapeseed

```json
// CloudFront cache behavior rules (simplified)
{
  "cache_behaviors": [
    {
      "path_pattern": "/static/*",
      "cache_policy": "24 hours",
      "comment": "JS, CSS, fonts — change only on deployment"
    },
    {
      "path_pattern": "/lessons/*/video/*",
      "cache_policy": "7 days",
      "origin": "S3",
      "comment": "Grapeseed lesson videos — rarely change"
    },
    {
      "path_pattern": "/api/public/*",
      "cache_policy": "5 minutes",
      "comment": "Public API responses — unit list, school info by subdomain"
    },
    {
      "path_pattern": "/api/*",
      "cache_policy": "no-cache",
      "forward_headers": ["Authorization"],
      "comment": "Authenticated API calls — never cache in CDN"
    }
  ]
}
```

---

## 7. MediatR Caching Pipeline Behavior

One of the most elegant patterns in a MediatR-based architecture is adding caching as a **pipeline behavior**. Instead of each handler manually checking ElastiCache, you create a behavior that does it automatically for any query that implements `ICachedQuery`.

```csharp
// ─────────────────────────────────────────────────────────────────
// ICachedQuery.cs — Marker interface for cacheable queries
// ─────────────────────────────────────────────────────────────────
public interface ICachedQuery
{
    /// <summary>
    /// The ElastiCache key for this query's result.
    /// Should include all parameters that affect the result.
    /// </summary>
    string CacheKey { get; }

    /// <summary>
    /// How long to cache this query's result in ElastiCache.
    /// </summary>
    TimeSpan CacheDuration { get; }
}
```

```csharp
// ─────────────────────────────────────────────────────────────────
// Behaviors/CachingBehavior.cs — Automatic caching for MediatR queries
// ─────────────────────────────────────────────────────────────────
public class CachingBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : ICachedQuery
{
    private readonly IDistributedCache _cache;
    private readonly ILogger<CachingBehavior<TRequest, TResponse>> _logger;

    public CachingBehavior(
        IDistributedCache cache,
        ILogger<CachingBehavior<TRequest, TResponse>> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        // Try to get from ElastiCache
        var cachedJson = await _cache.GetStringAsync(request.CacheKey, cancellationToken);
        if (cachedJson is not null)
        {
            _logger.LogDebug("Cache HIT for key: {CacheKey}", request.CacheKey);
            return JsonSerializer.Deserialize<TResponse>(cachedJson)!;
        }

        // Cache miss — execute the actual handler
        _logger.LogDebug("Cache MISS for key: {CacheKey}. Executing handler...", request.CacheKey);
        var response = await next();

        // Store the result in ElastiCache
        var json = JsonSerializer.Serialize(response);
        await _cache.SetStringAsync(
            request.CacheKey,
            json,
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = request.CacheDuration
            },
            cancellationToken);

        return response;
    }
}
```

```csharp
// ─────────────────────────────────────────────────────────────────
// Usage: A cached query — no cache code in the handler!
// ─────────────────────────────────────────────────────────────────

// The query declares its own cache key and duration
public record GetLessonContentQuery(string Unit, int LessonNumber, string SchoolId)
    : IRequest<LessonContentResponse>, ITenantRequest, ICachedQuery
{
    // Cache key includes SchoolId because different schools may have
    // customized lesson content (premium feature)
    public string CacheKey => $"lesson-content:{SchoolId}:{Unit}:{LessonNumber}";
    public TimeSpan CacheDuration => TimeSpan.FromHours(4); // Lesson content is stable
    public string SchoolId { get; set; } = SchoolId; // Set by TenantValidationBehavior
}

// The handler is completely clean — no cache logic at all
// CachingBehavior handles it transparently in the pipeline
public class GetLessonContentQueryHandler
    : IRequestHandler<GetLessonContentQuery, LessonContentResponse>
{
    private readonly ILessonRepository _repository;

    public GetLessonContentQueryHandler(ILessonRepository repository)
        => _repository = repository;

    public async Task<LessonContentResponse> Handle(
        GetLessonContentQuery request,
        CancellationToken ct)
    {
        // This only runs on cache miss — the behavior intercepts on cache hit
        var lesson = await _repository.GetLessonAsync(request.Unit, request.LessonNumber);
        return new LessonContentResponse { /* ... */ };
    }
}
```

The MediatR pipeline for this query looks like this:

```
Incoming: GetLessonContentQuery
    │
    ▼ LoggingBehavior: "Handling GetLessonContentQuery"
    │
    ▼ TenantValidationBehavior: validates school, stamps SchoolId
    │
    ▼ CachingBehavior:
        ├── Check ElastiCache for "lesson-content:school-bkk-001:Unit3:5"
        ├── HIT → return cached data immediately (handler never called)
        └── MISS → call next (handler executes, result stored in ElastiCache)
    │
    ▼ GetLessonContentQueryHandler: queries RDS PostgreSQL (only on MISS)
```

Every lesson load after the first one is served from ElastiCache in ~1ms, regardless of how many students are accessing it simultaneously.

---

## 8. RDS at Scale — Read Replicas and Connection Pooling

### Read Replicas for PostgreSQL

In Grapeseed, reads vastly outnumber writes (students reading lesson content vs. updating progress). RDS Read Replicas offload read traffic from the primary:

```
Write Operations (15% of traffic):
  Teacher saves lesson assignment → Primary RDS PostgreSQL
  Student submits quiz → Primary RDS PostgreSQL

Read Operations (85% of traffic):
  Students loading lesson content → Read Replica 1 or 2
  Teachers viewing class dashboards → Read Replica 1 or 2
  Progress reports → Read Replica 2 (dedicated for reporting)

┌──────────────────────┐
│    Primary RDS PG    │ ← Write endpoint: grapeseed-db.xxxx.rds.amazonaws.com
│    (Multi-AZ)        │
└──────────┬───────────┘
           │ Async replication (~50-200ms lag)
    ┌──────┴──────┐
    │             │
┌───▼──┐      ┌───▼──┐
│Replica│      │Replica│
│  1    │      │  2    │ ← Read endpoint: grapeseed-db.xxxx.ap-southeast-1.rds.amazonaws.com
│(AZ-b) │      │(AZ-c) │   (RDS automatically load-balances across all replicas)
└───────┘      └───────┘
```

```csharp
// ─────────────────────────────────────────────────────────────────
// Program.cs — Register separate write and read DbContexts
// ─────────────────────────────────────────────────────────────────

// Write context → Primary RDS endpoint
builder.Services.AddDbContext<GrapeseekWriteDbContext>(options =>
    options.UseNpgsql(builder.Configuration["ConnectionStrings:GrapeseekPrimary"]));

// Read context → RDS Read Replica cluster endpoint
// RDS automatically distributes reads across available replicas
builder.Services.AddDbContext<GrapeseekReadDbContext>(options =>
    options.UseNpgsql(builder.Configuration["ConnectionStrings:GrapeseekReadReplica"])
           .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)); // Read-only: skip change tracking
```

```csharp
// ─────────────────────────────────────────────────────────────────
// MediatR handlers use the appropriate context
// ─────────────────────────────────────────────────────────────────

// QUERY handler → Read Replica (high frequency, read-only)
public class GetStudentProgressQueryHandler
    : IRequestHandler<GetStudentProgressQuery, StudentProgressResponse>
{
    private readonly GrapeseekReadDbContext _readDb; // ← Read Replica

    public GetStudentProgressQueryHandler(GrapeseekReadDbContext readDb)
        => _readDb = readDb;

    public async Task<StudentProgressResponse> Handle(
        GetStudentProgressQuery request, CancellationToken ct)
    {
        // Executes on Read Replica — no load on Primary
        return await _readDb.Students
            .Where(s => s.Id == request.StudentId)
            .Select(s => new StudentProgressResponse { /* ... */ })
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException($"Student {request.StudentId} not found");
    }
}

// COMMAND handler → Primary (write operations)
public class SubmitLessonProgressCommandHandler
    : IRequestHandler<SubmitLessonProgressCommand, SubmitProgressResponse>
{
    private readonly GrapeseekWriteDbContext _writeDb; // ← Primary

    public SubmitLessonProgressCommandHandler(GrapeseekWriteDbContext writeDb)
        => _writeDb = writeDb;

    public async Task<SubmitProgressResponse> Handle(
        SubmitLessonProgressCommand command, CancellationToken ct)
    {
        var progress = new LessonProgress
        {
            StudentId = command.StudentId,
            Unit = command.Unit,
            LessonNumber = command.LessonNumber,
            ScorePercent = command.ScorePercent,
            IsCompleted = command.ScorePercent >= 70,
            CompletedAt = DateTime.UtcNow
        };
        // Saves to Primary — replicated to read replicas asynchronously
        _writeDb.LessonProgress.Add(progress);
        await _writeDb.SaveChangesAsync(ct);
        return new SubmitProgressResponse { IsCompleted = progress.IsCompleted };
    }
}
```

> **⚠️ Replication Lag:** Read replicas lag behind the primary by 50-200ms. If a teacher submits a grade and immediately views the class report, they might briefly see the old value. This is acceptable for Grapeseed's reporting features. For data that must be immediately consistent after a write (like the confirmation after quiz submission), read from the primary by using `GrapeseekWriteDbContext`.

### RDS Proxy — Managing Connection Pools

ECS Fargate Auto Scaling can create 50+ task instances. Each task has its own connection pool to RDS. At 50 tasks × 100 connections each = 5,000 connections. PostgreSQL on a `db.r6g.xlarge` handles about 3,000 connections before performance degrades.

**AWS RDS Proxy** solves this: it pools connections at the AWS layer, so all your ECS tasks share a managed connection pool:

```
50 ECS Tasks × 100 connections = 5,000 connection requests to RDS Proxy
RDS Proxy maintains only 200 actual connections to RDS

ECS Task → RDS Proxy (connection multiplexing) → RDS PostgreSQL
```

Add RDS Proxy by changing only the connection string — no code changes needed:

```
# Before: Direct to RDS
grapeseed-db.cluster-xxxx.ap-southeast-1.rds.amazonaws.com:5432

# After: Via RDS Proxy
grapeseed-db.proxy-xxxx.ap-southeast-1.rds.amazonaws.com:5432
```

---

## 9. Async Processing with Amazon SQS

Amazon SQS (Simple Queue Service) is a fully managed message queue. In Grapeseed, it decouples heavy or time-consuming work from the HTTP request path — keeping user-facing responses fast even when background work is slow.

### Why SQS for Grapeseed?

- **Managed by AWS:** No servers to run. No RabbitMQ cluster to maintain.
- **Durable:** Messages persist in SQS until a consumer processes them. If the consumer ECS task is restarting, messages queue up safely.
- **Scales automatically:** SQS handles any message volume without configuration.
- **Dead Letter Queue (DLQ):** Messages that fail processing repeatedly go to a DLQ for investigation — no lost data.

### Publishing to SQS

```csharp
// Install: dotnet add package AWSSDK.SQS
//          dotnet add package Amazon.Extensions.NETCore.Setup

// ─────────────────────────────────────────────────────────────────
// Program.cs — Register AWS SQS client
// ─────────────────────────────────────────────────────────────────
builder.Services.AddAWSService<IAmazonSQS>();
builder.Services.AddSingleton<IGrapeseekEventBus, SqsEventBus>();
```

```csharp
// ─────────────────────────────────────────────────────────────────
// SqsEventBus.cs — Publishing messages to SQS queues
// ─────────────────────────────────────────────────────────────────
public class SqsEventBus : IGrapeseekEventBus
{
    private readonly IAmazonSQS _sqsClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SqsEventBus> _logger;

    public SqsEventBus(IAmazonSQS sqsClient, IConfiguration configuration, ILogger<SqsEventBus> logger)
    {
        _sqsClient = sqsClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task PublishAsync<T>(T message, string queueName, CancellationToken ct = default)
        where T : class
    {
        var queueUrl = _configuration[$"AWS:SQS:Queues:{queueName}"];
        var messageBody = JsonSerializer.Serialize(message);

        var request = new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = messageBody,
            // Add message attributes for filtering and tracing
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["MessageType"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = typeof(T).Name
                },
                ["SchoolId"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = (message as ITenantMessage)?.SchoolId ?? "unknown"
                }
            }
        };

        var response = await _sqsClient.SendMessageAsync(request, ct);
        _logger.LogInformation("Published {MessageType} to SQS. MessageId: {MessageId}",
            typeof(T).Name, response.MessageId);
    }
}
```

```csharp
// ─────────────────────────────────────────────────────────────────
// MediatR Command Handler — uses SQS for fire-and-forget work
// ─────────────────────────────────────────────────────────────────
public class SubmitLessonProgressCommandHandler
    : IRequestHandler<SubmitLessonProgressCommand, SubmitProgressResponse>
{
    private readonly GrapeseekWriteDbContext _db;
    private readonly IGrapeseekEventBus _eventBus;

    public async Task<SubmitProgressResponse> Handle(
        SubmitLessonProgressCommand command,
        CancellationToken ct)
    {
        // FAST PATH: Save progress to RDS (~10ms)
        var progress = new LessonProgress
        {
            StudentId = command.StudentId,
            Unit = command.Unit,
            LessonNumber = command.LessonNumber,
            ScorePercent = command.ScorePercent,
            IsCompleted = command.ScorePercent >= 70,
            CompletedAt = DateTime.UtcNow
        };
        _db.LessonProgress.Add(progress);
        await _db.SaveChangesAsync(ct);

        // ASYNC PATH: Publish event to SQS for background processing (~5ms to enqueue)
        // This doesn't block the student from seeing their result.
        // Even if the consumer is temporarily down, SQS holds the message safely.
        await _eventBus.PublishAsync(new LessonCompletedMessage
        {
            SchoolId = command.SchoolId,
            StudentId = command.StudentId,
            Unit = command.Unit,
            LessonNumber = command.LessonNumber,
            ScorePercent = command.ScorePercent,
            IsCompleted = progress.IsCompleted,
            OccurredAt = progress.CompletedAt.Value
        }, queueName: "lesson-completed", ct);

        // Return result immediately — email/certificate/analytics happen in background
        return new SubmitProgressResponse
        {
            IsCompleted = progress.IsCompleted,
            ScorePercent = progress.ScorePercent,
            Message = progress.IsCompleted
                ? $"🎉 Congratulations! You passed Unit {command.Unit}, Lesson {command.LessonNumber}!"
                : "Keep practicing! Review the lesson and try again."
        };
    }
}
```

```csharp
// ─────────────────────────────────────────────────────────────────
// SqsConsumerBackgroundService.cs — Runs in the NotificationService ECS task
// Reads from SQS and processes events using MediatR
// ─────────────────────────────────────────────────────────────────
public class SqsConsumerBackgroundService : BackgroundService
{
    private readonly IAmazonSQS _sqsClient;
    private readonly IServiceProvider _serviceProvider;
    private readonly string _queueUrl;
    private readonly ILogger<SqsConsumerBackgroundService> _logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SQS Consumer started. Listening on queue: {QueueUrl}", _queueUrl);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Long-polling: waits up to 20 seconds for messages (reduces empty API calls)
            var receiveRequest = new ReceiveMessageRequest
            {
                QueueUrl = _queueUrl,
                MaxNumberOfMessages = 10,    // Process up to 10 messages per poll
                WaitTimeSeconds = 20,        // Long-polling — efficient, reduces AWS cost
                MessageAttributeNames = new List<string> { "All" }
            };

            var response = await _sqsClient.ReceiveMessageAsync(receiveRequest, stoppingToken);

            foreach (var message in response.Messages)
            {
                await ProcessMessageAsync(message, stoppingToken);
            }
        }
    }

    private async Task ProcessMessageAsync(Message sqsMessage, CancellationToken ct)
    {
        // Create a new DI scope for each message (like a separate HTTP request)
        using var scope = _serviceProvider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        try
        {
            var lessonCompleted = JsonSerializer.Deserialize<LessonCompletedMessage>(sqsMessage.Body);
            if (lessonCompleted is null) return;

            // Use MediatR to dispatch to the appropriate notification handler
            await mediator.Send(new SendLessonCompletionNotificationCommand(lessonCompleted), ct);

            // Delete from SQS only after successful processing
            await _sqsClient.DeleteMessageAsync(_queueUrl, sqsMessage.ReceiptHandle, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process SQS message {MessageId}. Will retry.", sqsMessage.MessageId);
            // Don't delete — SQS will make it visible again after visibility timeout
            // After N retries, SQS moves it to the Dead Letter Queue for investigation
        }
    }
}
```

---

## 10. Rate Limiting — Protecting Grapeseed from Overload

Rate limiting ensures no single school, user, or misbehaving client can exhaust Grapeseed's resources.

```csharp
// ─────────────────────────────────────────────────────────────────
// Program.cs — Rate limiting configuration for Grapeseed
// ─────────────────────────────────────────────────────────────────
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Global per-user rate limit: 200 requests per second
    // Prevents any single user from overwhelming the API
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        // Partition by user ID (authenticated) or IP (anonymous)
        var userId = context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                     ?? context.Connection.RemoteIpAddress?.ToString()
                     ?? "anon";

        return RateLimitPartition.GetFixedWindowLimiter(userId, _ =>
            new FixedWindowRateLimiterOptions
            {
                PermitLimit = 200,                 // 200 requests...
                Window = TimeSpan.FromSeconds(1),  // ...per second
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 20
            });
    });

    // Per-school rate limit: no school can exceed 2,000 requests per second
    // Prevents one large school from degrading performance for all others
    options.AddFixedWindowLimiter("per-school", limiterOptions =>
    {
        limiterOptions.PermitLimit = 2000;
        limiterOptions.Window = TimeSpan.FromSeconds(1);
    });

    // Quiz submission: max 5 submissions per minute per student (prevent spam)
    options.AddFixedWindowLimiter("quiz-submit", limiterOptions =>
    {
        limiterOptions.PermitLimit = 5;
        limiterOptions.Window = TimeSpan.FromMinutes(1);
    });

    // Report generation: max 10 per hour per teacher (reports are expensive)
    options.AddTokenBucketLimiter("report-generate", limiterOptions =>
    {
        limiterOptions.TokenLimit = 10;
        limiterOptions.ReplenishmentPeriod = TimeSpan.FromMinutes(6); // 10 per hour
        limiterOptions.TokensPerPeriod = 1;
        limiterOptions.AutoReplenishment = true;
    });
});

var app = builder.Build();
app.UseRateLimiter();
```

```csharp
// Apply specific limits to endpoints
[HttpPost("{unit}/{lessonNumber:int}/submit")]
[EnableRateLimiting("quiz-submit")]
public async Task<IActionResult> SubmitLessonProgress(
    string unit, int lessonNumber, SubmitProgressRequest request)
{
    var result = await _mediator.Send(new SubmitLessonProgressCommand(
        unit, lessonNumber, request.ScorePercent));
    return Ok(result);
}

[HttpPost("reports/class-progress")]
[EnableRateLimiting("report-generate")]
public async Task<IActionResult> GenerateClassReport(GenerateReportRequest request)
{
    var result = await _mediator.Send(new GenerateClassProgressReportCommand(request));
    return Ok(result);
}
```

> **Note:** AWS WAF (Web Application Firewall) can also enforce rate limiting at the CloudFront layer, before requests even reach your ECS tasks. This is more cost-effective for blocking abusive clients at the edge.

---

## 11. Background Jobs with Hangfire on AWS

Hangfire provides persistent, reliable background job scheduling for Grapeseed. Unlike fire-and-forget SQS messages, Hangfire jobs are stored in a database — if the server crashes mid-execution, the job is retried automatically.

```csharp
// Install: dotnet add package Hangfire.AspNetCore
//          dotnet add package Hangfire.PostgreSql (for storing jobs in RDS)

// ─────────────────────────────────────────────────────────────────
// Program.cs — Hangfire with RDS PostgreSQL backend
// ─────────────────────────────────────────────────────────────────
builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    // Jobs are stored in a dedicated table in RDS PostgreSQL
    // They survive ECS task restarts and redeployments
    .UsePostgreSqlStorage(builder.Configuration["ConnectionStrings:HangfireDb"]));

builder.Services.AddHangfireServer(options =>
{
    options.WorkerCount = 5;
    options.Queues = new[] { "critical", "default", "bulk" };
});

var app = builder.Build();
// Protect the dashboard — only accessible to platform admins
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new HangfireAdminOnlyAuthFilter() }
});

// ─────────────────────────────────────────────────────────────────
// Schedule recurring Grapeseed jobs at startup
// ─────────────────────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var scheduler = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();

    // Weekly progress reports — every Monday at 7 AM UTC (2 PM Bangkok time)
    scheduler.AddOrUpdate<WeeklyProgressReportJob>(
        "grapeseed-weekly-reports",
        job => job.GenerateForAllActiveSchoolsAsync(),
        "0 7 * * MON",
        TimeZoneInfo.Utc);

    // Daily: sync Grapeseed curriculum updates from the content CDN
    scheduler.AddOrUpdate<CurriculumSyncJob>(
        "grapeseed-curriculum-sync",
        job => job.SyncFromContentServiceAsync(),
        Cron.Daily(hour: 3));

    // Daily: expire inactive student sessions in ElastiCache
    scheduler.AddOrUpdate<SessionCleanupJob>(
        "grapeseed-session-cleanup",
        job => job.CleanupExpiredSessionsAsync(),
        Cron.Daily(hour: 4));

    // Hourly: update ElastiCache with fresh school feature flag configs
    // (in case a school upgraded their license)
    scheduler.AddOrUpdate<SchoolConfigRefreshJob>(
        "grapeseed-config-refresh",
        job => job.RefreshAllSchoolConfigsAsync(),
        Cron.Hourly());
}
```

```csharp
// ─────────────────────────────────────────────────────────────────
// Jobs/WeeklyProgressReportJob.cs
// Uses MediatR internally to dispatch report generation commands
// ─────────────────────────────────────────────────────────────────
public class WeeklyProgressReportJob
{
    private readonly ISchoolRepository _schools;
    private readonly IMediator _mediator;
    private readonly ILogger<WeeklyProgressReportJob> _logger;

    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 300, 900, 3600 })]
    public async Task GenerateForAllActiveSchoolsAsync()
    {
        var schools = await _schools.GetAllActiveAsync();
        _logger.LogInformation("Generating weekly reports for {Count} schools", schools.Count);

        // Process schools in parallel but limit concurrency
        // to avoid overwhelming RDS with simultaneous report queries
        var semaphore = new SemaphoreSlim(10); // Max 10 concurrent reports
        var tasks = schools.Select(async school =>
        {
            await semaphore.WaitAsync();
            try
            {
                // Dispatch to MediatR handler for clean separation of concerns
                await _mediator.Send(new GenerateWeeklyReportCommand(school.SchoolId));
                _logger.LogInformation("Weekly report complete for {SchoolId}", school.SchoolId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Weekly report failed for {SchoolId}", school.SchoolId);
                // Don't rethrow — allow other schools' reports to complete
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

## 12. The Grapeseed Scenario — Exam Day

With all high-load patterns applied, let's trace exam day:

```
08:00:00 — 180,000 students open Grapeseed simultaneously.

SCHEDULED SCALING (pre-configured for Monday morning):
- ECS tasks already pre-warmed to 15 instances since midnight
- RDS Proxy connections pre-established
- ElastiCache warmed up from previous day's traffic

CLOUDFRONT (handles ~65% of requests):
- Login page, CSS, JavaScript → served from nearest edge (Bangkok, Ho Chi Minh City, etc.)
- Lesson images → served from CloudFront / S3
- Latency: 8-15ms. No ECS task involved.

ALB + ECS AUTO SCALING:
- Traffic spike detected. CPU average across tasks climbs to 80%.
- ECS Auto Scaling triggers: adds 5 tasks every 60 seconds.
- By 08:03, running 35 tasks. CPU stabilizes at 65%.

ELASTICACHE (handles ~80% of authenticated requests):
- Student profiles: HIT (loaded by morning session)
- Lesson content: HIT (CachingBehavior in MediatR pipeline)
- School configs: HIT (30-minute TTL, loaded from previous day's sessions)
- Cache response: ~1ms. RDS barely touched.

MEDIATR PIPELINE:
- Every lesson request: LoggingBehavior → TenantValidationBehavior → CachingBehavior → Handler
- CachingBehavior: 85% HIT rate on lesson content
- Only 15% of requests reach the Handler and query RDS Read Replica

RDS (for the 15% of cache misses):
- Read Replicas handle all SELECT queries (lesson content, student profiles)
- Primary only receives writes (progress submissions)
- RDS Proxy ensures stable connection count despite 35 ECS tasks

SQS (background processing):
- Every quiz submission publishes "LessonCompleted" message to SQS
- NotificationService processes email queue asynchronously
- Students get their results in <100ms; emails arrive within 30-120 seconds

08:30:00 — Traffic normalizes as most students finish their assessments.
- ECS Auto Scaling begins scaling in (scale-in cooldown: 5 minutes)
- By 09:00: back to 8 tasks
- Cost for exam day extra capacity: ~$45 USD (2 hours × 20 extra tasks × Fargate pricing)

RESULT:
- 180,000 students served
- Average latency: P50 = 48ms, P95 = 185ms, P99 = 620ms
- Zero downtime. Zero manual intervention.
- Platform admin sees green dashboards all morning.
```

---

## 13. Decision Guide

| Technique | When to Apply | AWS Service |
|-----------|--------------|------------|
| CloudFront CDN | Always, from day 1 | Amazon CloudFront |
| ECS Auto Scaling | From the start (configure min/max) | ECS Application Auto Scaling |
| ElastiCache | Any data read more than once per minute | Amazon ElastiCache (Redis) |
| MediatR CachingBehavior | Any query returning stable data | Works with ElastiCache |
| RDS Read Replicas | When DB reads are the bottleneck | Amazon RDS |
| RDS Proxy | When connection count is the bottleneck | Amazon RDS Proxy |
| SQS Async Processing | For non-user-blocking background work | Amazon SQS |
| Hangfire | For reliable scheduled/recurring jobs | Hangfire + RDS PostgreSQL |
| Rate Limiting | Always, to protect the system | ASP.NET Core Rate Limiting |
| AWS WAF | When you need edge-level blocking | AWS WAF + CloudFront |

---

## 14. Summary and Key Takeaways

### Core Concepts

| Concept | One-Line Summary |
|---------|-----------------|
| ECS Auto Scaling | Automatically adds/removes Fargate tasks based on CPU/memory |
| CloudFront | Cache static assets and video at the CDN edge — students get content from nearby |
| ElastiCache | Shared Redis cache — lesson content, school configs, student profiles |
| MediatR CachingBehavior | Automatic caching in the pipeline — handlers stay clean |
| RDS Read Replicas | Offload 85% of read queries from the primary database |
| RDS Proxy | Multiplex thousands of ECS connections into a manageable pool |
| Amazon SQS | Durable async message queue — decouple background work from user requests |
| Hangfire + RDS | Persistent scheduled jobs that survive restarts |
| Rate Limiting | Per-user and per-school limits protect the platform from abuse |

### The Five Rules of High Load Engineering

1. **Cache aggressively** — CloudFront + ElastiCache + MediatR CachingBehavior. Every cache hit is an RDS query you didn't need.
2. **Scale horizontally on ECS** — Stateless tasks + ALB + Auto Scaling = elastic capacity.
3. **Decouple with SQS** — Email, certificates, analytics: none of this should block the student's "lesson submitted" response.
4. **Index your SchoolId** — Every query filters by SchoolId. Without a composite index, you're scanning millions of rows.
5. **Prepare before the spike** — Use Scheduled Scaling for known exam days. Pre-warming is better than reacting.

*→ Continue to: [Chapter 4 — Microservices & MediatR](./book_ch4_microservices.md)*

---

*Chapter 3 Complete · 14 sections · High Load Systems on AWS*
