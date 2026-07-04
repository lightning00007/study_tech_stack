# Chapter 1: Distributed Systems

> **Foundational Architecture · Core Theory · Production Patterns**
> *"A distributed system is one in which the failure of a computer you didn't even know existed can render your own computer unusable."*
> — Leslie Lamport, Turing Award winner, inventor of distributed consensus

---

## Table of Contents

1. [Introduction — Why One Computer Is Never Enough](#1-introduction)
2. [What Is a Distributed System?](#2-what-is-a-distributed-system)
3. [The 8 Fallacies of Distributed Computing](#3-the-8-fallacies)
4. [The CAP Theorem — Choosing Your Tradeoffs](#4-cap-theorem)
5. [Consistency Models — How "Fresh" Is Fresh Enough?](#5-consistency-models)
6. [Fault Tolerance — Designing for Failure](#6-fault-tolerance)
7. [The Circuit Breaker Pattern](#7-circuit-breaker)
8. [Distributed Caching with Redis](#8-distributed-caching)
9. [Service Discovery — Finding Each Other in the Network](#9-service-discovery)
10. [The Education Platform Scenario](#10-education-platform-scenario)
11. [Decision Guide — When to Go Distributed](#11-decision-guide)
12. [Summary and Key Takeaways](#12-summary)

---

## 1. Introduction — Why One Computer Is Never Enough

Let's start with a story.

Imagine it's the first day of a new school year. LinguaLearn has just signed contracts with 500 schools across Southeast Asia. Ten thousand students log in simultaneously to access their first English lesson. Your backend runs on a single powerful server — a beefy machine with 64 CPU cores, 256 GB of RAM, and fast NVMe storage.

The server handles the first thousand users fine. At two thousand, response times start climbing. At five thousand, the CPU is pegged at 100%. At seven thousand, the database connection pool is exhausted and requests start failing. At ten thousand, the server runs out of memory and crashes. Every single student sees an error page. The school administrators are furious. Your phone is ringing.

**This is the fundamental problem that distributed systems solve.**

No matter how powerful a single computer is, it has hard limits:
- **CPU limit** — A CPU can only execute so many instructions per second
- **Memory limit** — RAM is finite
- **Network limit** — A single network card can only push so many bytes per second
- **Storage I/O limit** — Even NVMe drives have maximum read/write throughput

When your user base grows beyond what one machine can handle, you need to spread the work across **multiple machines**. That collection of machines working together as if they were one system is a **distributed system**.

The catch? The moment you add a second machine, you've introduced an entirely new category of problems that simply don't exist when you run everything on one computer. This chapter teaches you what those problems are, why they exist, and how experienced engineers deal with them.

---

## 2. What Is a Distributed System?

A **distributed system** is a group of independent computers that communicate over a network to accomplish a shared goal, and appear to end users as a single, unified system.

The key phrase is *appear to end users as a single system*. When a student opens LinguaLearn and watches a video lesson, they don't know — and shouldn't care — that:
- Their authentication was handled by a server in Singapore
- The lesson metadata was fetched from a server in Tokyo
- The video was streamed from a CDN node in Jakarta
- Their progress was saved to a database cluster in Hong Kong

From the student's perspective, they just opened the app and watched a lesson. The distributed system hides all that complexity.

### What Makes a System "Distributed"?

According to the classic definition by Andrew Tanenbaum, a distributed system has three properties:

1. **Multiple autonomous components** — Multiple independent machines, each with its own processor and memory
2. **Communicate via network** — Components talk to each other only through messages over a network
3. **Single coherent system** — They present a unified interface to users

### Why Are Distributed Systems Hard?

The difficulty comes from **uncertainty**. When you call a function on the same machine, you get an answer or an exception — there's no ambiguity. When you send a message to another machine over a network, any of these could happen:

- The message arrives and the other machine processes it ✅
- The message is lost in transit — the other machine never gets it ❌
- The message arrives, but the other machine crashes before responding ❌
- The other machine processes it AND sends a response, but the response is lost ❌
- The other machine is very slow and your request times out — but the machine is still processing it ❌

In the last case, **you simply cannot know** whether the operation succeeded or not. This fundamental uncertainty is what makes distributed systems so intellectually challenging and why entire careers are built around understanding them.

---

## 3. The 8 Fallacies of Distributed Computing

In the early 1990s, engineers at Sun Microsystems — having spent years building distributed software — compiled a list of assumptions that developers commonly (and wrongly) make. These became known as **The 8 Fallacies of Distributed Computing**.

Understanding these fallacies will save you from some of the most painful and hard-to-debug production failures you'll ever encounter.

---

### Fallacy 1: The Network Is Reliable

**What developers think:** "I'll just make an HTTP call to fetch the lesson data. It'll work."

**Reality:** Networks fail. Cables get cut. Routers crash. Cloud providers have outages. Packets get dropped. Your call to the Lesson Service might fail 0.1% of the time — which sounds tiny until you're making 10,000 calls per minute, giving you 10 failures every minute.

**What to do:** Always assume network calls can fail. Implement **retries with exponential backoff**, **timeouts**, and **fallback behavior**.

---

### Fallacy 2: Latency Is Zero

**What developers think:** "The other service is in the same data center, so calling it is basically instant."

**Reality:** Even in the same data center, a network call takes microseconds to milliseconds. Across data centers? Tens to hundreds of milliseconds. A call from London to Sydney? ~300ms of round-trip time just from physics (the speed of light). If your code makes 20 sequential service calls to render a page, and each takes 50ms, that's a full second of latency before any of your own code runs.

**What to do:** Minimize network round trips. Use **batching**, **caching**, and **parallel calls** where possible.

---

### Fallacy 3: Bandwidth Is Infinite

**What developers think:** "I'll just return the full student object with all their lesson history every time."

**Reality:** Network bandwidth costs money and has limits. Sending huge payloads repeatedly is slow and expensive. A student object with 5 years of lesson history might be 500KB. Sending that to 10,000 concurrent users is 5GB of data — per request cycle.

**What to do:** Return only the data you need. Use pagination, field selection, and compression.

---

### Fallacy 4: The Network Is Secure

**What developers think:** "The services are on a private internal network, so they're safe."

**Reality:** Internal networks get compromised. Malicious actors can get inside your network. Configuration mistakes expose internal services. Even "private" traffic can be intercepted.

**What to do:** Use **mutual TLS (mTLS)** between services, authenticate service-to-service calls, and never trust a request just because it came from inside the network.

---

### Fallacy 5: Topology Doesn't Change

**What developers think:** "I'll hardcode the IP address of the database server."

**Reality:** Servers get replaced. Services move between machines. IP addresses change during deployments, auto-scaling, and failures. In cloud environments, machines are ephemeral — they can be destroyed and recreated at any time with new IP addresses.

**What to do:** Use **service discovery** and **DNS-based routing** instead of hardcoded IPs. (We cover this in Section 9.)

---

### Fallacy 6: There Is Only One Administrator

**What developers think:** "We control the whole system, so we can coordinate changes easily."

**Reality:** Large systems have multiple teams, multiple cloud regions, multiple infrastructure providers, and multiple deployment pipelines. The "admin" of the video streaming infrastructure might be a completely different team from the "admin" of the lesson database.

**What to do:** Design systems that can tolerate independent upgrades, partial deployments, and version mismatches.

---

### Fallacy 7: Transport Cost Is Zero

**What developers think:** "Data transfer between services is free."

**Reality:** Cloud providers charge for data egress (traffic leaving a region or availability zone). Internal data center transfers use CPU for serialization and deserialization. Network infrastructure has costs.

**What to do:** Be mindful of what data crosses service boundaries and how often.

---

### Fallacy 8: The Network Is Homogeneous

**What developers think:** "All our services use the same tech stack and communicate the same way."

**Reality:** Large organizations end up with a mix of technologies, protocols, and data formats. Your Lesson Service might use REST/JSON. Your Video Service might use gRPC/Protobuf. Your legacy system might speak SOAP. They all need to interoperate.

**What to do:** Define clear **interface contracts** between services. Use an **API gateway** to handle protocol translation.

---

## 4. The CAP Theorem — Choosing Your Tradeoffs

The **CAP Theorem**, proven by computer scientist Eric Brewer in 2000, is one of the most important theoretical results in distributed systems. It states:

> **In a distributed system, you can guarantee at most two of the following three properties simultaneously:**
> - **C**onsistency
> - **A**vailability
> - **P**artition tolerance

Let's understand each one using our education platform.

### Consistency (C)

**Definition:** Every read receives the most recent write, or an error. All nodes in the system see the same data at the same time.

**Education Example:** A teacher marks a student's quiz as "passed." In a consistent system, the very next second when that student checks their progress dashboard, they will see "passed" — regardless of which server handles their request.

### Availability (A)

**Definition:** Every request receives a response (not necessarily the most recent data), but the system never returns an error just because it's overwhelmed or a node is down.

**Education Example:** Even if one of your database servers is down for maintenance, students can still log in and access lessons. They might see slightly old data (e.g., a quiz score from an hour ago), but they get *some* response.

### Partition Tolerance (P)

**Definition:** The system continues to function even when network partitions occur — that is, when some machines can't communicate with others because a network link broke.

**Education Example:** Your Singapore and Tokyo servers can't reach each other because an undersea cable was damaged. Students in Singapore can still use the platform. Students in Tokyo can still use the platform. The two halves operate independently.

### The Cruel Tradeoff

Here's the uncomfortable truth: **Network partitions happen. You cannot prevent them.** Therefore, Partition Tolerance is not optional for any real distributed system — you must have P.

That leaves you choosing between **C and A**:

```
┌────────────────────────────────────────────────────────┐
│                   CAP Theorem Choices                   │
│                                                         │
│   Since P is required, your real choice is:            │
│                                                         │
│   CP — Consistent + Partition Tolerant                 │
│   When a partition occurs, some nodes will refuse      │
│   requests to avoid returning stale data.              │
│   → Use for: financial transactions, payment records   │
│   → Example: MongoDB, HBase, Apache ZooKeeper         │
│                                                         │
│   AP — Available + Partition Tolerant                  │
│   When a partition occurs, nodes keep serving          │
│   requests, but some might return stale data.          │
│   → Use for: lesson content, video metadata, profiles  │
│   → Example: Cassandra, CouchDB, DynamoDB             │
└────────────────────────────────────────────────────────┘
```

### Applying CAP to LinguaLearn

Different parts of our system have different requirements:

| Data | Choose | Why |
|------|--------|-----|
| Student payment records | **CP** | A double-charge is catastrophic. Stale data = wrong billing. |
| Teacher-assigned grades | **CP** | A student's official grade must be accurate. |
| Lesson content (text, videos) | **AP** | If a lesson was updated 5 minutes ago and a student sees the old version briefly, that's acceptable. |
| Student progress (lessons watched) | **AP** | Approximate is fine. A 30-second delay in progress sync is invisible to users. |
| Live class session state | **CP** | In a live class, everyone must see the same state. |

> **Key Insight:** You don't choose one CAP mode for your entire application. Different features can — and should — use different databases and consistency models based on their specific requirements.

---

## 5. Consistency Models — How "Fresh" Is Fresh Enough?

CAP introduced two extremes: strong consistency vs. availability during partitions. In practice, there's a rich spectrum of consistency models between those two poles.

### Strong Consistency

After a write completes, all subsequent reads — from any node in the cluster — return that new value.

```
Write: lesson.title = "Past Perfect Tense" → Server A
Read from Server B → "Past Perfect Tense" ✅ (immediately)
Read from Server C → "Past Perfect Tense" ✅ (immediately)
```

**Cost:** To achieve this, nodes must coordinate before responding. This adds latency and means during a network partition, some nodes must refuse requests.

### Eventual Consistency

After a write, the new value will *eventually* propagate to all nodes, but there's no guarantee of when. For a short window, different nodes might return different values.

```
Write: student.lastLesson = "Unit 5"  → Server A (Singapore)
Read from Server B (Tokyo, 100ms later) → "Unit 4"  (not yet propagated)
Read from Server B (2 seconds later)  → "Unit 5"  ✅ (now propagated)
```

**Benefit:** Much higher availability and lower latency. Writes don't need global coordination.

**Challenge:** Your application code must be written to tolerate brief inconsistencies. This is called being **eventually consistent-aware**.

### Read-Your-Writes Consistency (Session Consistency)

A weaker but very practical model: after a user writes data, that same user always reads their own latest write. Other users might still see old data temporarily.

```
Student A submits quiz answers → saved
Student A immediately checks their grade → sees their submission ✅
Student B checks Student A's grade → might see "pending" for a few seconds ✅ (acceptable)
```

This is the sweet spot for many features in our education platform: students should always see their own actions reflected immediately, but data propagating to other users or dashboards can have a short delay.

---

## 6. Fault Tolerance — Designing for Failure

Here is the mindset shift every distributed systems engineer must make:

> **Stop asking: "How do I prevent failures?"**
> **Start asking: "How do I make my system survive failures?"**

Failures are not exceptional events in distributed systems — they are the norm. In a cluster of 1,000 machines, if each machine has a 0.1% chance of failing per day, you should expect **one machine failure per day**. Your system must keep running.

### The Four Strategies of Fault Tolerance

#### 1. Redundancy — "Don't Have a Single Point of Failure"

Run multiple instances of every component. If one dies, the others keep working.

```
Without Redundancy:         With Redundancy:
                           
  [Student] ──► [Server]     [Student] ──► [Server 1]
                   ↓                       [Server 2]  ← Load Balancer
                 CRASH                     [Server 3]
                   ↓
              EVERYTHING STOPS
```

#### 2. Replication — "Keep Copies of Your Data"

Store the same data on multiple database nodes. If one database server fails, another has all the data.

```
Write to Primary DB
        ↓ (async replication)
    Replica 1  ←  Read traffic
    Replica 2  ←  Read traffic
    Replica 3  ←  Failover candidate
```

#### 3. Retry with Backoff — "Try Again, But Politely"

If a call fails, try again. But don't retry instantly in a tight loop — that can overwhelm a struggling service.

```
Attempt 1: Failed → wait 1 second
Attempt 2: Failed → wait 2 seconds  
Attempt 3: Failed → wait 4 seconds
Attempt 4: Failed → wait 8 seconds
Attempt 5: Failed → give up, return error to user
```

This is called **exponential backoff with jitter**. The jitter (a small random addition to each wait time) prevents all clients from retrying simultaneously, which would create a thundering herd that overwhelms the recovering service.

#### 4. Graceful Degradation — "Do Less, Not Nothing"

When a dependency fails, serve a degraded but still-useful response instead of failing completely.

**Example:** The Recommendation Service (which suggests which lesson to study next) goes down. Instead of showing an error page, the Lesson Service falls back to showing a generic "Continue where you left off" button with the student's last lesson. The experience is degraded, but the student can still study.

---

## 7. The Circuit Breaker Pattern

The circuit breaker is one of the most important patterns in distributed systems. It prevents **cascading failures** — a situation where one failing service brings down all the services that depend on it.

### The Problem Without Circuit Breakers

Imagine LinguaLearn's Lesson Service needs to call the Video Service to get video metadata. The Video Service suddenly starts responding very slowly (taking 30 seconds per request) because its database is struggling.

Without a circuit breaker:
1. Lesson Service makes a request to Video Service
2. The thread waits... and waits... for 30 seconds
3. Meanwhile, more student requests come in
4. Each request spawns a thread that's now stuck waiting on Video Service
5. After thousands of requests, all threads in the Lesson Service are occupied waiting
6. The Lesson Service itself stops responding
7. The API Gateway starts timing out on Lesson Service calls
8. Now the entire platform appears down — even though only the Video Service had issues

This is called a **cascading failure**, and it's one of the most common causes of large-scale outages.

### How the Circuit Breaker Works

The circuit breaker is modeled on an electrical circuit breaker. It has three states:

```
   ┌──────────────────────────────────────────────────────────────┐
   │                   Circuit Breaker States                      │
   │                                                               │
   │   CLOSED (Normal)          OPEN (Failing)                    │
   │   ─────────────            ─────────────                     │
   │   Requests flow            Requests fail immediately         │
   │   normally. Failures       (no waiting, no hanging threads)  │
   │   are counted.             After a timeout, moves to...      │
   │                                         ↓                    │
   │                         HALF-OPEN (Testing)                  │
   │                         ─────────────────                    │
   │                         Allow one request through.           │
   │                         If it succeeds → back to CLOSED      │
   │                         If it fails    → back to OPEN        │
   └──────────────────────────────────────────────────────────────┘
   
   CLOSED ──(5+ failures in 10 sec)──► OPEN
   OPEN   ──(after 30 seconds)──────► HALF-OPEN
   HALF-OPEN ──(success)────────────► CLOSED
   HALF-OPEN ──(failure)────────────► OPEN
```

### Implementing a Circuit Breaker in C# with Polly

[Polly](https://github.com/App-vNext/Polly) is the standard library for resilience and transient fault handling in .NET. It makes circuit breakers, retries, and timeouts easy to implement.

```csharp
// Install: dotnet add package Polly
// Install: dotnet add package Microsoft.Extensions.Http.Polly

// ─────────────────────────────────────────────────────────────────
// Program.cs — Registering resilience policies
// ─────────────────────────────────────────────────────────────────
using Polly;
using Polly.Extensions.Http;

var builder = WebApplication.CreateBuilder(args);

// Register the VideoServiceClient with HttpClient + Polly policies
builder.Services.AddHttpClient<IVideoServiceClient, VideoServiceClient>(client =>
{
    client.BaseAddress = new Uri("https://video-service.lingualearn.internal");
    client.Timeout = TimeSpan.FromSeconds(10); // Never wait more than 10 seconds
})
.AddPolicyHandler(GetRetryPolicy())
.AddPolicyHandler(GetCircuitBreakerPolicy());

// ─────────────────────────────────────────────────────────────────
// Retry Policy: Try up to 3 times with exponential backoff + jitter
// ─────────────────────────────────────────────────────────────────
static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
{
    var jitter = new Random();
    
    return HttpPolicyExtensions
        .HandleTransientHttpError()  // Handles 5xx errors and network exceptions
        .WaitAndRetryAsync(
            retryCount: 3,
            sleepDurationProvider: retryAttempt =>
            {
                // Exponential backoff: 1s, 2s, 4s — plus a small random jitter
                var exponentialWait = TimeSpan.FromSeconds(Math.Pow(2, retryAttempt));
                var jitterAmount = TimeSpan.FromMilliseconds(jitter.Next(0, 300));
                return exponentialWait + jitterAmount;
            },
            onRetry: (outcome, timespan, retryAttempt, context) =>
            {
                // Log each retry attempt for observability
                Console.WriteLine($"[Retry] Attempt {retryAttempt} after {timespan.TotalSeconds:F1}s " +
                                  $"due to: {outcome.Exception?.Message ?? outcome.Result.StatusCode.ToString()}");
            }
        );
}

// ─────────────────────────────────────────────────────────────────
// Circuit Breaker Policy
// Opens after 5 failures in 10 seconds; stays open for 30 seconds
// ─────────────────────────────────────────────────────────────────
static IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy()
{
    return HttpPolicyExtensions
        .HandleTransientHttpError()
        .CircuitBreakerAsync(
            handledEventsAllowedBeforeBreaking: 5,   // Open after 5 failures
            durationOfBreak: TimeSpan.FromSeconds(30), // Stay open for 30 seconds
            onBreak: (outcome, breakDelay) =>
            {
                // Alert your monitoring system when the circuit opens!
                Console.WriteLine($"[Circuit Breaker] OPENED for {breakDelay.TotalSeconds}s " +
                                  $"due to: {outcome.Exception?.Message ?? outcome.Result.StatusCode.ToString()}");
            },
            onReset: () =>
            {
                Console.WriteLine("[Circuit Breaker] CLOSED — service recovered.");
            },
            onHalfOpen: () =>
            {
                Console.WriteLine("[Circuit Breaker] HALF-OPEN — testing recovery...");
            }
        );
}
```

```csharp
// ─────────────────────────────────────────────────────────────────
// VideoServiceClient.cs — Client that uses the configured policies
// ─────────────────────────────────────────────────────────────────
public interface IVideoServiceClient
{
    Task<VideoMetadata?> GetVideoMetadataAsync(string videoId);
}

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
            // This call automatically uses the retry + circuit breaker policies
            // registered in Program.cs via AddPolicyHandler()
            var response = await _httpClient.GetAsync($"/api/videos/{videoId}/metadata");
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadFromJsonAsync<VideoMetadata>();
        }
        catch (BrokenCircuitException)
        {
            // The circuit is open — Video Service is known to be down
            // Return a fallback immediately instead of waiting
            _logger.LogWarning("Circuit is OPEN for VideoService. Returning fallback metadata.");
            return GetFallbackMetadata(videoId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch video metadata for {VideoId} after all retries", videoId);
            return null;
        }
    }

    // Graceful degradation: return minimal data when the service is unavailable
    private VideoMetadata GetFallbackMetadata(string videoId)
    {
        return new VideoMetadata
        {
            VideoId = videoId,
            Title = "Video Lesson",        // Generic title
            DurationSeconds = 0,           // Unknown duration
            IsAvailable = false,           // Signal to the UI to show a "temporarily unavailable" message
            FallbackUsed = true
        };
    }
}
```

The beauty of this pattern is that **the Lesson Service stays healthy even when the Video Service goes down**. Instead of hanging threads consuming all resources, failing requests fail fast (in microseconds, once the circuit is open), and the rest of the application continues to work normally.

---

## 8. Distributed Caching with Redis

A **cache** is a fast, temporary data store that sits in front of a slower, authoritative data store. By serving frequently-requested data from the cache, you can dramatically reduce load on your database and improve response times.

In a single-server application, you might use an in-memory cache (like `IMemoryCache` in .NET). But in a distributed system with multiple application servers, you need a **distributed cache** — a cache that is shared across all your servers.

**Redis** (Remote Dictionary Server) is the industry-standard distributed cache. It stores data in memory (making it extremely fast), supports rich data structures, and can be replicated for high availability.

### Why In-Memory Cache Isn't Enough in a Distributed System

```
                    WITHOUT Distributed Cache
                    
   Request → Server 1 → Cache MISS → DB → fills Server1 cache → returns data
   Request → Server 2 → Cache MISS → DB → fills Server2 cache → returns data
   Request → Server 3 → Cache MISS → DB → fills Server3 cache → returns data
   
   Problem: Every server has its own cache. The same data is loaded from DB
   multiple times. Cache invalidation must happen on every server separately.
   
                    WITH Distributed Cache (Redis)
                    
   Request → Server 1 → Cache MISS → DB → fills Redis → returns data
   Request → Server 2 → Cache HIT from Redis → returns data immediately ✅
   Request → Server 3 → Cache HIT from Redis → returns data immediately ✅
   
   One shared cache for all servers. Invalidate once, all servers see it.
```

### Implementing Distributed Cache in C#

```csharp
// Install: dotnet add package Microsoft.Extensions.Caching.StackExchangeRedis

// ─────────────────────────────────────────────────────────────────
// Program.cs — Register Redis distributed cache
// ─────────────────────────────────────────────────────────────────
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = builder.Configuration.GetConnectionString("Redis");
    // e.g., "redis-cluster.lingualearn.internal:6379,password=secret,ssl=true"
    options.InstanceName = "LinguaLearn:"; // Namespace prefix for all keys
});

builder.Services.AddScoped<ILessonCacheService, LessonCacheService>();
```

```csharp
// ─────────────────────────────────────────────────────────────────
// LessonCacheService.cs — Cache-Aside pattern for lesson data
// ─────────────────────────────────────────────────────────────────
// The Cache-Aside pattern (also called Lazy Loading) is the most
// common caching pattern:
//   1. Check the cache first
//   2. If hit: return cached data
//   3. If miss: load from DB, store in cache, return data
// ─────────────────────────────────────────────────────────────────

public interface ILessonCacheService
{
    Task<Lesson?> GetLessonAsync(int lessonId);
    Task InvalidateLessonAsync(int lessonId);
}

public class LessonCacheService : ILessonCacheService
{
    private readonly IDistributedCache _cache;
    private readonly ILessonRepository _repository;
    private readonly ILogger<LessonCacheService> _logger;

    // How long should we cache a lesson before refreshing from the DB?
    // Lesson content changes rarely, so 1 hour is a reasonable TTL.
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

        // Step 1: Try to get from cache
        var cachedJson = await _cache.GetStringAsync(cacheKey);

        if (cachedJson is not null)
        {
            // Cache HIT: deserialize and return. No DB call needed.
            _logger.LogDebug("Cache HIT for lesson {LessonId}", lessonId);
            return JsonSerializer.Deserialize<Lesson>(cachedJson);
        }

        // Step 2: Cache MISS — load from the database
        _logger.LogDebug("Cache MISS for lesson {LessonId}. Loading from DB...", lessonId);
        var lesson = await _repository.GetByIdAsync(lessonId);

        if (lesson is null) return null;

        // Step 3: Store in cache for future requests
        var json = JsonSerializer.Serialize(lesson);
        await _cache.SetStringAsync(cacheKey, json, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = LessonCacheTtl
        });

        return lesson;
    }

    public async Task InvalidateLessonAsync(int lessonId)
    {
        // When a teacher updates a lesson, we must remove the cached version
        // so the next request will load the fresh data from the DB.
        var cacheKey = $"lesson:{lessonId}";
        await _cache.RemoveAsync(cacheKey);
        _logger.LogInformation("Cache invalidated for lesson {LessonId}", lessonId);
    }
}
```

```csharp
// ─────────────────────────────────────────────────────────────────
// LessonController.cs — Using the cache service
// ─────────────────────────────────────────────────────────────────
[ApiController]
[Route("api/lessons")]
public class LessonController : ControllerBase
{
    private readonly ILessonCacheService _cacheService;

    public LessonController(ILessonCacheService cacheService)
    {
        _cacheService = cacheService;
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Lesson>> GetLesson(int id)
    {
        var lesson = await _cacheService.GetLessonAsync(id);
        return lesson is null ? NotFound() : Ok(lesson);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateLesson(int id, UpdateLessonRequest request)
    {
        // ... update the lesson in DB ...
        
        // After updating, invalidate the cache so next read gets fresh data
        await _cacheService.InvalidateLessonAsync(id);
        return NoContent();
    }
}
```

---

## 9. Service Discovery — Finding Each Other in the Network

In a distributed system, services need to find each other. But as we learned in Fallacy 5, you can't hardcode IP addresses — machines change. **Service discovery** is the mechanism by which services register themselves and find each other dynamically.

### How It Works

```
┌─────────────────────────────────────────────────────────────────┐
│                    Service Discovery Flow                         │
│                                                                   │
│  1. Service starts up → registers itself with Service Registry   │
│     "I am VideoService, running at 10.0.1.42:8080, healthy"     │
│                                                                   │
│  2. Client wants to call VideoService:                           │
│     - Query the Registry: "Where is VideoService?"              │
│     - Registry returns: ["10.0.1.42:8080", "10.0.1.55:8080"]   │
│     - Client picks one and makes the call                       │
│                                                                   │
│  3. If a service crashes:                                        │
│     - It stops sending health check heartbeats                   │
│     - Registry removes it from the list                         │
│     - No more traffic is sent to the dead instance              │
└─────────────────────────────────────────────────────────────────┘
```

In modern cloud deployments, **Kubernetes** handles service discovery automatically through its DNS system. Services are registered as Kubernetes Services and are addressable by name (e.g., `http://video-service.default.svc.cluster.local`). The Kubernetes DNS system handles routing behind the scenes.

In a non-Kubernetes environment, **Consul** by HashiCorp is a popular service registry.

For our examples throughout this book, we'll use the Kubernetes-style DNS approach, which is what most production systems use today.

---

## 10. The Education Platform Scenario

Let's put everything together. Here is how LinguaLearn's core lesson-delivery flow works using distributed system principles:

```
Student in Vietnam opens the LinguaLearn mobile app and taps "Start Lesson 12"

1. The request hits the CDN (Cloudflare) edge node in Singapore.
   Static assets (images, CSS, JS) are served from the CDN cache immediately.
   
2. The API call goes to the API Gateway, which routes it to the Lesson Service.
   The Lesson Service has 3 instances running across 2 availability zones.
   The Load Balancer picks the least-busy instance.

3. Lesson Service checks Redis distributed cache for Lesson 12.
   CACHE HIT — the lesson content was loaded earlier, served in ~2ms.

4. Lesson Service calls Video Service to get the video URL for Lesson 12.
   The Polly circuit breaker checks: is Video Service healthy?
   YES — the call goes through. Video URL returned.
   
   (If Video Service was failing, the circuit breaker would return a fallback 
   URL pointing to the last known-good video URL or a "video temporarily 
   unavailable" message. The lesson still loads.)

5. Progress Service is called asynchronously (fire-and-forget) to record 
   that the student started Lesson 12.
   This call goes through a message queue. Even if Progress Service is 
   temporarily down, the event is queued and processed when it recovers.

6. All data is assembled and returned to the student in ~50ms.
   The student taps Play. The video streams from the nearest CDN node.
   
Total time: ~50ms for the lesson metadata. Video streaming starts in ~200ms.
The student sees no loading spinner.
```

---

## 11. Decision Guide — When to Go Distributed

Before you add distributed complexity to your system, make sure you actually need it. Distributed systems are powerful but significantly more complex to build, deploy, and debug than single-server systems.

### When to Add Distribution

| Signal | Action |
|--------|--------|
| Single server CPU is consistently > 70% | Add horizontal scaling |
| Database queries are taking > 100ms regularly | Add read replicas and caching |
| A single service failure takes down everything | Add redundancy and circuit breakers |
| Deploy one component causes downtime for others | Separate into independent services |
| Teams are blocked on each other for deployments | Consider microservices boundaries |
| Users in different continents experience high latency | Add geo-distributed nodes / CDN |

### When NOT to Add Distribution (yet)

| Signal | Action |
|--------|--------|
| You have fewer than 10,000 daily active users | Scale vertically first — it's simpler |
| Your team has fewer than 5 engineers | Monolith is easier to maintain |
| You are still finding product-market fit | Don't optimize prematurely |
| Your current server is at 20% CPU usage | You have headroom — don't complicate it yet |

> **The Golden Rule:** A well-optimized monolith can serve millions of users. Instagram ran on 3 engineers and a PostgreSQL database when it was acquired by Facebook for $1 billion, serving 13 million users. Start simple. Add distribution when you have concrete evidence you need it.

---

## 12. Summary and Key Takeaways

You have covered the fundamentals of distributed systems. Here is what to carry with you:

### Core Concepts

| Concept | One-Line Summary |
|---------|-----------------|
| Distributed System | Multiple computers working together as one coherent system |
| 8 Fallacies | The network is NOT reliable, zero-latency, or free. Design accordingly. |
| CAP Theorem | You can't have strong consistency AND high availability during partitions. Choose. |
| Eventual Consistency | Data will be consistent... eventually. Often good enough for education content. |
| Fault Tolerance | Design to survive failures, not just prevent them. |
| Circuit Breaker | Fail fast when a dependency is down to prevent cascading failures. |
| Distributed Cache | Share cached data across all server instances via Redis. |
| Service Discovery | Services find each other dynamically, not via hardcoded IPs. |

### The Three Rules of Distributed Systems

1. **Expect failure** — Every call can fail. Every service can go down. Design for it.
2. **Embrace eventual consistency** — Not everything needs to be perfectly consistent all the time. Know which data does and which doesn't.
3. **Observe everything** — You can't debug what you can't see. Log every call, trace every request, monitor every service.

### What's Next

You now understand how distributed systems work and why they are challenging. In the next chapter, we tackle a problem that sits on top of distribution: how do you serve many different schools (tenants) from the same system while keeping their data completely isolated?

*→ Continue to: [Chapter 2 — Multi-Tenant Architecture](./book_ch2_multi_tenant_systems.md)*

---

*Chapter 1 Complete · 12 sections · Distributed Systems Fundamentals*
