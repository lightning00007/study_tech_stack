# Chapter 1: Distributed Systems

> **Foundational Architecture · Core Theory · AWS Production Patterns**
> *"A distributed system is one in which the failure of a computer you didn't even know existed can render your own computer unusable."*
> — Leslie Lamport, Turing Award winner

---

## Table of Contents

1. [Introduction — Why One Server Is Never Enough](#1-introduction)
2. [What Is a Distributed System?](#2-what-is-a-distributed-system)
3. [The 8 Fallacies of Distributed Computing](#3-the-8-fallacies)
4. [The CAP Theorem — Choosing Your Tradeoffs](#4-cap-theorem)
5. [Consistency Models — How "Fresh" Is Fresh Enough?](#5-consistency-models)
6. [Fault Tolerance — Designing for Failure](#6-fault-tolerance)
7. [The Circuit Breaker Pattern with Polly](#7-circuit-breaker)
8. [Distributed Caching with Amazon ElastiCache (Redis)](#8-distributed-caching)
9. [Service Discovery on AWS](#9-service-discovery)
10. [The Grapeseed Scenario](#10-grapeseed-scenario)
11. [Decision Guide — When to Go Distributed](#11-decision-guide)
12. [Summary and Key Takeaways](#12-summary)

---

## 1. Introduction — Why One Server Is Never Enough

Let's start with a story.

Grapeseed has just signed partnership agreements with 200 schools across Southeast Asia. On the first Monday of the new school year, ten thousand students log into the platform simultaneously to access their first English lesson. Your backend runs on a single EC2 instance — even a powerful `m5.4xlarge` with 16 vCPUs and 64 GB of RAM.

The server handles the first thousand users fine. At three thousand, CPU climbs to 80%. At six thousand, the RDS database connection pool is exhausted — new queries start failing with `Npgsql.NpgsqlException: The connection pool has been exhausted`. At eight thousand, the application server runs out of memory. At ten thousand, the EC2 instance becomes unresponsive. AWS health checks fail. ECS marks it as unhealthy. Every student sees an error page. Your Slack starts lighting up.

**This is the fundamental problem that distributed systems solve.**

No matter how powerful a single computer is, it has hard physical limits — CPU cores, RAM, network bandwidth, disk I/O. When your user base grows beyond what one machine can handle, you need to spread the work across **multiple machines working together**. That collection of machines, coordinated to appear as a single system to end users, is a **distributed system**.

The catch? The moment you add a second machine, you have introduced a new category of problems that simply do not exist on a single server. This chapter teaches you what those problems are, why they exist, and how to solve them — using the Grapeseed stack on AWS.

---

## 2. What Is a Distributed System?

A **distributed system** is a group of independent computers that communicate over a network to accomplish a shared goal, and appear to end users as a single, unified system.

When a student opens Grapeseed and starts a lesson, they don't know — and shouldn't care — that:
- Their authentication was validated by an ECS task in `us-east-1`
- The lesson metadata was fetched from a different ECS task reading an RDS PostgreSQL read replica
- The lesson video was streamed from Amazon S3 via a CloudFront edge node in their country
- Their progress was saved asynchronously via an SQS message

From the student's perspective, they just opened the app and started learning. The distributed system hides all that complexity.

### What Makes a System "Distributed"?

1. **Multiple autonomous components** — Multiple independent machines, each with its own processor and memory
2. **Communicate via network** — Components talk through messages over a network
3. **Single coherent system** — They present a unified interface to users

### Why Are Distributed Systems Hard?

The difficulty comes from **uncertainty**. When you call a function locally, you get an answer or an exception. When you send a message to another machine over a network, any of these can happen:

- The message arrives and is processed ✅
- The message is lost in transit ❌
- The message arrives, but the remote service crashes before responding ❌
- The response is sent but lost on the way back to you ❌
- The remote service is slow and your request times out — but it's still processing ❌

In the last case, **you simply cannot know** whether the operation succeeded. This fundamental uncertainty is what makes distributed systems so challenging.

---

## 3. The 8 Fallacies of Distributed Computing

Engineers at Sun Microsystems compiled a list of wrong assumptions developers commonly make when building distributed software. Understanding these will save you from the most painful production incidents.

---

### Fallacy 1: The Network Is Reliable

**What developers think:** "I'll just make an HTTP call from LessonService to VideoService. It'll work."

**Reality:** Networks fail. Even within AWS, availability zones can have connectivity issues. An HTTP call might fail 0.1% of the time — but at 10,000 calls per minute, that's 10 failures per minute.

**What to do:** Implement **retries with exponential backoff** (Polly), **timeouts**, and **fallback behavior**. Never assume a network call will succeed.

---

### Fallacy 2: Latency Is Zero

**What developers think:** "Both services are in the same AWS region, so it's basically instant."

**Reality:** Even within the same AWS region, a network hop takes 1-5ms. Cross-region calls (Singapore to US East) can take 150-200ms. If your code makes 15 sequential service calls to render a page, and each takes 10ms, that's 150ms of pure network overhead before any of your own logic runs.

**What to do:** Minimize round trips. Use **batching**, **parallel calls** (`Task.WhenAll`), and **caching**.

---

### Fallacy 3: Bandwidth Is Infinite

**What developers think:** "I'll return the complete student object with all lesson history every time."

**Reality:** AWS charges for data transfer between services (data egress). Sending large payloads repeatedly is slow and costly. A student object with 3 years of Grapeseed lesson history could be hundreds of kilobytes.

**What to do:** Return only the data needed. Use **pagination** and **projection** (EF Core's `Select()` to return DTOs with only needed fields).

---

### Fallacy 4: The Network Is Secure

**What developers think:** "Services communicate on a private VPC, so it's safe."

**Reality:** Even internal AWS VPC traffic can be misconfigured or intercepted. IAM misconfigurations, security group mistakes, and insider threats are real.

**What to do:** Use **AWS VPC security groups** to limit which services can talk to which. Use **AWS IAM roles** for service authentication. Encrypt sensitive data even in transit within the VPC.

---

### Fallacy 5: Topology Doesn't Change

**What developers think:** "I'll hardcode the RDS endpoint IP in the connection string."

**Reality:** ECS task IPs change on every deployment. RDS endpoints change during failovers. Auto-scaling creates and destroys instances constantly.

**What to do:** Use **DNS-based discovery** — always reference services by name (e.g., `lesson-service.grapeseed.internal` or the RDS endpoint hostname, not its IP).

---

### Fallacy 6: There Is Only One Administrator

**What developers think:** "We control all the infrastructure, so we can coordinate changes easily."

**Reality:** At Grapeseed's scale, the lesson team, the video team, and the infrastructure team all have independent deployment pipelines. The AWS account might have multiple teams with different access levels.

**What to do:** Design for independent deployments. Services must tolerate each other being on different versions simultaneously.

---

### Fallacy 7: Transport Cost Is Zero

**What developers think:** "Network calls between AWS services in the same region are free."

**Reality:** AWS charges for data transfer out of a region and between availability zones. Transferring large files between ECS tasks and S3 frequently adds up.

**What to do:** Be intentional about what data crosses service and AZ boundaries. Cache aggressively to reduce repeated transfers.

---

### Fallacy 8: The Network Is Homogeneous

**What developers think:** "All our services speak REST/JSON."

**Reality:** Grapeseed likely integrates with school district systems (which might speak SOAP or have proprietary APIs), third-party video providers, and external authentication providers. They don't all speak the same language.

**What to do:** Define clear interface contracts. Use the API Gateway to handle protocol translation and request normalization.

---

## 4. The CAP Theorem — Choosing Your Tradeoffs

The **CAP Theorem**, proven by Eric Brewer in 2000, states:

> **In a distributed system, you can guarantee at most two of the following three properties simultaneously:**
> - **C**onsistency
> - **A**vailability
> - **P**artition tolerance

### The Three Properties — Grapeseed Context

**Consistency (C):** Every read receives the most recent write, or an error. All nodes see the same data simultaneously.

*Grapeseed example:* A teacher marks a student's Unit 3 quiz as complete. A consistent system ensures the next time anyone queries that student's progress — on any server — they see "Unit 3: Complete."

**Availability (A):** Every request receives a response (not necessarily the most recent data). The system never errors just because it's busy.

*Grapeseed example:* Even if one RDS replica is being restored from a snapshot, students can still log in and access lessons — they might see slightly stale progress data, but they get a response.

**Partition Tolerance (P):** The system keeps functioning even when some machines can't communicate with others.

*Grapeseed example:* If AWS's network inside a region has a partial failure and some ECS tasks can't reach others, the platform still serves users.

### The Cruel Truth

**Network partitions happen — you cannot prevent them.** Partition Tolerance is not optional for any real distributed system. This leaves you choosing between C and A:

```
┌──────────────────────────────────────────────────────────────┐
│                   Your Real CAP Choice                         │
│                                                               │
│  CP — Consistent + Partition Tolerant                        │
│  When a partition occurs, some nodes refuse requests         │
│  to prevent returning stale data.                            │
│  → Use for: payment processing, official grades              │
│  → AWS services: RDS Multi-AZ (strong consistency)           │
│                                                               │
│  AP — Available + Partition Tolerant                         │
│  When a partition occurs, nodes keep serving requests        │
│  but may return briefly stale data.                          │
│  → Use for: lesson content, video metadata, profiles         │
│  → AWS services: DynamoDB (eventual consistency mode),       │
│    ElastiCache Redis, RDS Read Replicas                      │
└──────────────────────────────────────────────────────────────┘
```

### Applying CAP to Grapeseed

| Data | Choose | Why |
|------|--------|-----|
| Student payment / subscription records | **CP** | A billing error is catastrophic |
| Official end-of-unit assessment grades | **CP** | Must be accurate for school records |
| Lesson content (text, media references) | **AP** | Stale by 1 hour is fine for a lesson page |
| "Currently watching" video progress | **AP** | Approximate is fine; sync every 30 seconds |
| Live class session state | **CP** | Everyone in the class must see the same state |
| Leaderboards / engagement stats | **AP** | A 5-minute lag is invisible to users |

> **Key Insight:** You don't pick one CAP mode for the whole application. Different features use different AWS services and consistency settings based on their specific requirements.

---

## 5. Consistency Models — How "Fresh" Is Fresh Enough?

### Strong Consistency

After a write, all subsequent reads from any node return the new value immediately.

```
Write: lesson.title = "Unit 4: Comparatives" → RDS Primary
Read from RDS Read Replica → "Unit 4: Comparatives" ✅ (blocked until replica syncs)
```

**Cost:** Coordinating across nodes adds latency and reduces availability during partitions. Use for financial records and official grades.

### Eventual Consistency

After a write, the new value will propagate to all nodes eventually, but not instantly.

```
Write: student.lastLesson = "Unit 3" → RDS Primary (Singapore)
Read from Read Replica (10ms later)  → "Unit 2"  (not yet propagated)
Read from Read Replica (1 second later) → "Unit 3" ✅ (now propagated)
```

**Benefit:** Much higher availability and lower latency. Writes don't block on global coordination.

**When good enough:** For Grapeseed, lesson content, video watch history, and progress dashboards can tolerate a few seconds of lag. Students don't notice if their "last watched" indicator is 2 seconds behind.

### Read-Your-Writes Consistency

After a user writes data, **that same user** always reads their own latest write. Other users might still see old data briefly.

```
Student submits a quiz → saved to RDS Primary
Student immediately clicks "My Results" → sees their submission ✅
Teacher views student results → might see "pending" for 1-2 seconds ✅ (acceptable)
```

This is the sweet spot for most Grapeseed features. Achieved by routing a user's reads to the primary immediately after a write, then returning to read replicas after a short window.

---

## 6. Fault Tolerance — Designing for Failure

> **Stop asking: "How do I prevent failures?"**
> **Start asking: "How does Grapeseed keep running when failures happen?"**

On AWS, hardware failures are expected. AWS itself publishes incident reports for every outage. Your system must be designed to survive them.

### 1. Redundancy — Multi-AZ Deployment

Never run a single instance of anything critical. On AWS, use **multiple Availability Zones**:

```
AWS Region: ap-southeast-1 (Singapore)
  ├── AZ: ap-southeast-1a
  │     ├── ECS Task: LessonService (instance 1)
  │     └── RDS PostgreSQL: Primary
  │
  ├── AZ: ap-southeast-1b
  │     ├── ECS Task: LessonService (instance 2)
  │     └── RDS PostgreSQL: Standby (auto-failover)
  │
  └── AZ: ap-southeast-1c
        └── ECS Task: LessonService (instance 3)

ALB routes requests across all 3 AZs.
If AZ 1a fails entirely, ALB stops sending traffic there.
RDS automatically promotes the standby to primary (~30 seconds).
Students might see a brief error, but service recovers automatically.
```

### 2. RDS Multi-AZ — Automatic Database Failover

When you enable Multi-AZ on RDS, AWS maintains a synchronous standby replica in a different AZ. If the primary fails, RDS automatically promotes the standby. The connection string (endpoint) remains the same — your application reconnects automatically.

```
Connection String: grapeseed-db.cluster-xxxx.ap-southeast-1.rds.amazonaws.com
   (This DNS always points to the current primary — AWS updates it during failover)
```

### 3. Retry with Exponential Backoff

If a call fails, try again — but politely. An immediate retry can overwhelm a recovering service.

```
Attempt 1: Failed → wait 1s + small random jitter
Attempt 2: Failed → wait 2s + jitter
Attempt 3: Failed → wait 4s + jitter
Attempt 4: Give up, return graceful error to user
```

The **jitter** (random milliseconds added to each wait) prevents all clients from retrying at exactly the same moment, which would create a thundering herd that overwhelms the recovering service.

### 4. Graceful Degradation

When a dependency fails, serve a degraded but still-useful response.

**Grapeseed Example:** The Recommendation Engine (suggests the next lesson) goes down. Instead of showing an error page, the Lesson Service falls back to: "Continue where you left off → Unit 3, Lesson 2." The experience is slightly worse, but the student can still study.

---

## 7. The Circuit Breaker Pattern with Polly

The circuit breaker prevents **cascading failures** — when one failing service drags all services that depend on it down with it.

### The Problem Without Circuit Breakers

LessonService calls VideoService to get video metadata. VideoService's RDS database is running a long migration and is very slow (30 seconds per query). Without a circuit breaker:

1. LessonService threads wait 30 seconds for VideoService to respond
2. More student requests arrive, spawning more waiting threads
3. LessonService runs out of threads — it stops responding entirely
4. The ALB health check fails on LessonService
5. Now both services appear down, even though only VideoService had an issue

This is a **cascading failure**. The circuit breaker prevents it by detecting the failing pattern and stopping calls fast.

### Circuit Breaker States

```
CLOSED (Normal) ──(5 failures in 10 seconds)──► OPEN (Failing)
                                                       │
                                          (wait 30 seconds)
                                                       │
                                                       ▼
                                              HALF-OPEN (Testing)
                                              Allow 1 request through
                                              Success → CLOSED
                                              Failure → OPEN
```

### Implementation with Polly

```csharp
// ─────────────────────────────────────────────────────────────────
// Program.cs — Registering Polly resilience on HttpClient
// Install: dotnet add package Microsoft.Extensions.Http.Polly
// ─────────────────────────────────────────────────────────────────
builder.Services.AddHttpClient<IVideoServiceClient, VideoServiceClient>(client =>
{
    // Use the ECS Service Connect DNS name, not a hardcoded IP
    client.BaseAddress = new Uri(builder.Configuration["ServiceUrls:VideoService"]);
    client.Timeout = TimeSpan.FromSeconds(10);
})
.AddPolicyHandler(ResiliencePolicies.GetRetryPolicy())
.AddPolicyHandler(ResiliencePolicies.GetCircuitBreakerPolicy());

// ─────────────────────────────────────────────────────────────────
// ResiliencePolicies.cs — Reusable Polly policies for Grapeseed
// ─────────────────────────────────────────────────────────────────
public static class ResiliencePolicies
{
    private static readonly Random _jitter = new();

    /// <summary>
    /// Retry up to 3 times with exponential backoff + jitter.
    /// Handles 5xx errors and network exceptions.
    /// </summary>
    public static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy() =>
        HttpPolicyExtensions
            .HandleTransientHttpError()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt =>
                {
                    var exponential = TimeSpan.FromSeconds(Math.Pow(2, attempt));  // 2s, 4s, 8s
                    var jitter = TimeSpan.FromMilliseconds(_jitter.Next(0, 300)); // ±300ms jitter
                    return exponential + jitter;
                },
                onRetry: (outcome, delay, attempt, _) =>
                {
                    var reason = outcome.Exception?.Message ?? outcome.Result.StatusCode.ToString();
                    Console.WriteLine($"[Polly Retry] Attempt {attempt} after {delay.TotalSeconds:F1}s. Reason: {reason}");
                }
            );

    /// <summary>
    /// Open circuit after 5 failures within 10 seconds.
    /// Stay open for 30 seconds before testing recovery.
    /// </summary>
    public static IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy() =>
        HttpPolicyExtensions
            .HandleTransientHttpError()
            .CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: 5,
                durationOfBreak: TimeSpan.FromSeconds(30),
                onBreak: (outcome, delay) =>
                {
                    var reason = outcome.Exception?.Message ?? outcome.Result.StatusCode.ToString();
                    Console.WriteLine($"[Circuit Breaker] OPEN for {delay.TotalSeconds}s. Reason: {reason}");
                    // TODO: Publish a CloudWatch metric alarm here
                },
                onReset: () => Console.WriteLine("[Circuit Breaker] CLOSED — service recovered."),
                onHalfOpen: () => Console.WriteLine("[Circuit Breaker] HALF-OPEN — testing recovery...")
            );
}
```

```csharp
// ─────────────────────────────────────────────────────────────────
// VideoServiceClient.cs — Uses the registered Polly policies
// ─────────────────────────────────────────────────────────────────
public class VideoServiceClient : IVideoServiceClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<VideoServiceClient> _logger;

    public VideoServiceClient(HttpClient httpClient, ILogger<VideoServiceClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<VideoMetadata?> GetVideoMetadataAsync(string videoId)
    {
        try
        {
            // Polly policies registered in Program.cs automatically apply here
            var response = await _httpClient.GetAsync($"/api/videos/{videoId}/metadata");
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<VideoMetadata>();
        }
        catch (BrokenCircuitException ex)
        {
            // Circuit is OPEN — Video Service is known to be failing
            // Return fallback data immediately, no waiting
            _logger.LogWarning(ex, "Circuit OPEN for VideoService. Returning fallback for {VideoId}", videoId);
            return CreateFallbackMetadata(videoId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get video metadata for {VideoId} after all retries", videoId);
            return null;
        }
    }

    /// <summary>
    /// Graceful degradation: return minimal metadata so the lesson page
    /// can still render with a "video temporarily unavailable" indicator.
    /// </summary>
    private static VideoMetadata CreateFallbackMetadata(string videoId) => new()
    {
        VideoId = videoId,
        Title = "Lesson Video",
        IsAvailable = false,         // Signal to UI: show "temporarily unavailable"
        FallbackUsed = true
    };
}
```

---

## 8. Distributed Caching with Amazon ElastiCache for Redis

### Why In-Memory Cache Isn't Enough

When ECS scales LessonService to 10 task instances, each instance has its own in-memory cache. The same lesson data gets loaded 10 times from RDS — once per instance. Cache invalidation becomes chaos: invalidate the lesson cache on Instance 1, but Instances 2-10 still serve stale data.

**Amazon ElastiCache for Redis** is a fully managed Redis cluster that all ECS instances share. Invalidate once — all instances see it.

```
Without ElastiCache:
  Student hits Instance 1 → Cache MISS → RDS query → fills Instance 1 cache
  Student hits Instance 2 → Cache MISS → RDS query → fills Instance 2 cache  (wasted!)
  Teacher updates a lesson → invalidates Instance 1 cache only
  Student hits Instance 2 → reads stale data  ❌

With ElastiCache:
  Student hits Instance 1 → Cache MISS → RDS query → fills ElastiCache
  Student hits Instance 2 → Cache HIT from ElastiCache → served in ~1ms ✅
  Teacher updates a lesson → invalidate ElastiCache key → all instances see it ✅
```

### Setting Up ElastiCache in C#

```csharp
// ─────────────────────────────────────────────────────────────────
// Program.cs — Register Amazon ElastiCache (Redis) via IDistributedCache
// Install: dotnet add package Microsoft.Extensions.Caching.StackExchangeRedis
// ─────────────────────────────────────────────────────────────────
builder.Services.AddStackExchangeRedisCache(options =>
{
    // ElastiCache Cluster endpoint from AWS Secrets Manager / SSM Parameter Store
    options.Configuration = builder.Configuration["AWS:ElastiCache:Endpoint"];
    // e.g., "grapeseed-cache.abc123.cache.amazonaws.com:6379,ssl=true"
    options.InstanceName = "Grapeseed:"; // Namespace prefix for all cache keys
});

builder.Services.AddScoped<ILessonCacheService, LessonCacheService>();
```

```csharp
// ─────────────────────────────────────────────────────────────────
// LessonCacheService.cs — Cache-Aside pattern for Grapeseed lessons
// The Cache-Aside pattern:
//   1. Check ElastiCache first
//   2. Cache HIT → return cached data (no DB call)
//   3. Cache MISS → load from RDS → store in ElastiCache → return
// ─────────────────────────────────────────────────────────────────
public class LessonCacheService : ILessonCacheService
{
    private readonly IDistributedCache _cache;
    private readonly ILessonRepository _repository;
    private readonly ILogger<LessonCacheService> _logger;

    // Grapeseed lesson content changes rarely — 1 hour TTL is appropriate.
    // If a teacher edits a lesson, we explicitly invalidate the key.
    private static readonly TimeSpan LessonCacheTtl = TimeSpan.FromHours(1);

    public LessonCacheService(
        IDistributedCache cache,
        ILessonRepository repository,
        ILogger<LessonCacheService> logger)
    {
        _cache = cache;
        _repository = repository;
        _logger = logger;
    }

    public async Task<Lesson?> GetLessonAsync(int lessonId)
    {
        var cacheKey = $"lesson:{lessonId}";

        // Step 1: Try ElastiCache
        var cachedJson = await _cache.GetStringAsync(cacheKey);
        if (cachedJson is not null)
        {
            _logger.LogDebug("ElastiCache HIT for lesson {LessonId}", lessonId);
            return JsonSerializer.Deserialize<Lesson>(cachedJson);
        }

        // Step 2: Cache miss — query RDS PostgreSQL
        _logger.LogDebug("ElastiCache MISS for lesson {LessonId}. Querying RDS...", lessonId);
        var lesson = await _repository.GetByIdAsync(lessonId);
        if (lesson is null) return null;

        // Step 3: Store in ElastiCache for future requests
        var json = JsonSerializer.Serialize(lesson);
        await _cache.SetStringAsync(cacheKey, json, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = LessonCacheTtl
        });

        return lesson;
    }

    public async Task InvalidateLessonAsync(int lessonId)
    {
        // Called when a teacher saves edits to a lesson
        await _cache.RemoveAsync($"lesson:{lessonId}");
        _logger.LogInformation("ElastiCache key invalidated for lesson {LessonId}", lessonId);
    }
}
```

### Reading Configuration Securely from AWS

In production, never put secrets in `appsettings.json`. Use **AWS Secrets Manager** or **SSM Parameter Store**:

```csharp
// ─────────────────────────────────────────────────────────────────
// Program.cs — Load configuration from AWS Secrets Manager
// Install: dotnet add package AWSSDK.SecretsManager
//          dotnet add package Kralizek.Extensions.Configuration.AWSSecretsManager
// ─────────────────────────────────────────────────────────────────
builder.Configuration.AddSecretsManager(region: RegionEndpoint.APSoutheast1, configurator: options =>
{
    // Load only secrets with the "grapeseed/" prefix
    options.SecretFilter = entry => entry.Name.StartsWith("grapeseed/lesson-service/");
    options.KeyGenerator = (entry, key) =>
        // Transform "grapeseed/lesson-service/RdsConnectionString"
        // into "ConnectionStrings:RdsConnectionString" (appsettings format)
        key.Replace("grapeseed/lesson-service/", "").Replace("/", ":");
});
```

---

## 9. Service Discovery on AWS

In ECS (Elastic Container Service), tasks get ephemeral IP addresses that change on every deployment. You cannot hardcode them. AWS provides several discovery mechanisms:

### ECS Service Connect (Recommended for Grapeseed)

ECS Service Connect registers each service under a DNS name within a namespace. Services call each other by name, and ECS handles routing:

```yaml
# ecs-task-definition.json excerpt
"serviceConnectConfiguration": {
  "enabled": true,
  "namespace": "grapeseed.internal",
  "services": [
    {
      "portName": "lesson-service-port",
      "discoveryName": "lesson-service",
      "clientAliases": [{ "port": 8080, "dnsName": "lesson-service" }]
    }
  ]
}
```

```csharp
// appsettings.json — Use DNS names, never IPs
{
  "ServiceUrls": {
    "VideoService":    "http://video-service:8080",    // ECS Service Connect DNS
    "ProgressService": "http://progress-service:8080",
    "NotifyService":   "http://notify-service:8080"
  }
}
```

ECS handles load balancing across all healthy instances of `video-service` automatically. If an instance becomes unhealthy, ECS stops routing to it within seconds.

### AWS Cloud Map (for Non-ECS Services)

If some components run outside ECS (e.g., a Lambda function, an on-premises integration), **AWS Cloud Map** provides a service registry that any AWS service can register with and query.

---

## 10. The Grapeseed Scenario

Let's trace a complete request through the Grapeseed distributed system:

```
A student in Thailand opens the Grapeseed app and taps "Start Unit 3, Lesson 1".

1. CloudFront (CDN) Edge Node (Bangkok):
   Static assets (app JS, CSS, images) → served from CloudFront cache.
   Latency: ~5ms. No ECS instance involved.

2. API Request → AWS API Gateway (Regional Endpoint):
   GET /api/lessons/unit/3/lesson/1
   API Gateway validates the JWT token (using Cognito or our IdentityService).
   Routes request to LessonService ALB target group.

3. ALB (Application Load Balancer) → LessonService:
   ALB uses Least Outstanding Requests algorithm.
   Routes to the least-busy ECS task.
   LessonService runs in ap-southeast-1 (Singapore) across 3 AZs.

4. LessonService processes the request:
   a. ElastiCache lookup → HIT (lesson was cached 20 minutes ago) → ~1ms.
   b. Calls VideoService via HTTP (ECS Service Connect DNS):
      - Polly checks: is circuit closed? YES.
      - HTTP GET http://video-service:8080/api/videos/v-unit3-les1/metadata
      - Response in ~15ms.
   c. Assembles lesson + video metadata into response.

5. Student's device receives response in ~50ms.
   They tap Play. Video streams from S3 via CloudFront.
   CloudFront serves the video from the nearest edge node.

6. Async (fire-and-forget via SQS):
   LessonService publishes "LessonStarted" message to SQS queue.
   ProgressService reads the queue and updates the student's progress.
   This happens in the background. The student doesn't wait for it.
```

---

## 11. Decision Guide — When to Go Distributed

### When to Add Distribution

| Signal | Action |
|--------|--------|
| Single ECS task CPU consistently > 70% | Enable ECS Auto Scaling |
| RDS query times > 100ms regularly | Add read replicas, add caching |
| Single AZ failure takes down everything | Deploy across 3 AZs with Multi-AZ RDS |
| Deploying one service forces redeploy of others | Separate into independent services |
| Teams are blocked on each other for releases | Consider microservices boundaries |
| Students in Asia experience high latency | Add CloudFront + regional ECS deployment |

### When NOT to Add Distribution Yet

| Signal | Action |
|--------|--------|
| Fewer than 5,000 daily active users | Scale vertically (upgrade EC2/RDS) |
| Team has fewer than 5 engineers | Stay with a modular monolith |
| Still defining core features | Don't optimize architecture prematurely |
| Current infra is at 20% capacity | You have plenty of headroom |

---

## 12. Summary and Key Takeaways

### Core Concepts

| Concept | One-Line Summary |
|---------|-----------------|
| Distributed System | Multiple computers working together as one coherent system |
| 8 Fallacies | The network is NOT reliable, zero-latency, or free. Design accordingly. |
| CAP Theorem | Choose between strong consistency and high availability during partitions. |
| Eventual Consistency | Data will be consistent eventually. Good enough for most Grapeseed content. |
| Fault Tolerance | Design to survive failures — especially AZ failures on AWS. |
| Circuit Breaker (Polly) | Fail fast when a dependency is down. Prevent cascading failures. |
| ElastiCache (Redis) | Distributed cache shared across all ECS instances. |
| ECS Service Connect | DNS-based service discovery — services find each other by name, not IP. |

### The Three Rules of Distributed Systems

1. **Expect failure** — AWS has outages. Design for them, not against them.
2. **Embrace eventual consistency** — Not everything needs to be perfectly consistent in real time.
3. **Observe everything** — Use CloudWatch + X-Ray. You can't debug what you can't see.

*→ Continue to: [Chapter 2 — Multi-Tenant Architecture](./book_ch2_multi_tenant_systems.md)*

---

*Chapter 1 Complete · 12 sections · Distributed Systems on AWS*
