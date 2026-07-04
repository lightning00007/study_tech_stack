# Chapter 4: Microservices Architecture

> **System Design · Service Decomposition · Team Organization**
> *"Microservices are not about technology. They are about organizational structure. Conway's Law says: Any organization that designs a system will produce a design whose structure is a copy of the organization's communication structure."*
> — Melvin Conway, 1967 (still painfully true today)

---

## Table of Contents

1. [Introduction — The Monolith That Grew Too Large](#1-introduction)
2. [Monolith vs. Microservices — An Honest Comparison](#2-monolith-vs-microservices)
3. [Domain-Driven Design — Finding the Boundaries](#3-domain-driven-design)
4. [Designing the LinguaLearn Services](#4-designing-lingualearn-services)
5. [Service Communication — Sync vs. Async](#5-service-communication)
6. [Building a Minimal API Service in C#](#6-building-a-minimal-api-service)
7. [The API Gateway — The Front Door](#7-api-gateway)
8. [The Event Bus — Async Communication with MassTransit](#8-event-bus)
9. [The Saga Pattern — Distributed Transactions](#9-saga-pattern)
10. [Resilience with Polly — Calling Other Services Safely](#10-resilience-with-polly)
11. [Distributed Tracing and Observability](#11-distributed-tracing)
12. [Service Mesh — Managing Service-to-Service Traffic](#12-service-mesh)
13. [The Education Platform Scenario — Student Enrollment](#13-education-platform-scenario)
14. [The Microservices Trap — When NOT to Use Them](#14-microservices-trap)
15. [Decision Guide and Migration Path](#15-decision-guide)
16. [Summary and Key Takeaways](#16-summary)

---

## 1. Introduction — The Monolith That Grew Too Large

LinguaLearn launched three years ago as a single application. One codebase, one database, one deployment. At the time, this was exactly right — the team was small, the feature set was simple, and moving fast mattered more than architectural purity.

But things change. The engineering team grew from 5 to 60 developers. Features multiplied: live video classes, AI pronunciation feedback, parent portals, school management dashboards, analytics reports. The codebase grew to 500,000 lines of code. And now, every deployment of the slightest change requires deploying the entire application, touching every team's work simultaneously.

Here is what a typical week looks like in the monolith:

- Team A (video features) is ready to deploy their new feature on Tuesday.
- Team B (quiz engine) has a bug in their code that was discovered Monday.
- Team C (reporting) pushed a change that breaks the authentication module.
- **Nobody can deploy until everything is fixed.** Three teams are blocked by one team's bug.

Meanwhile, the video feature needs 10 servers during video-heavy hours (2-4 PM), but the reporting feature only runs once a week on Sunday. Yet the whole application scales together — you can't scale just the video part.

A developer working on the quiz engine module needs to understand how the authentication module works, because they share the same database and the same codebase. Knowledge silos break down. The codebase becomes nobody's home territory and everybody's problem.

**This is what microservices solve.** Not just a technical problem — a people and organization problem.

---

## 2. Monolith vs. Microservices — An Honest Comparison

Before you rush to break up your application, you need an honest understanding of what you're gaining and what you're giving up.

### The Monolith

A monolith is a single deployable unit. All features, all business logic, all data access are in one application, sharing one database.

```
┌───────────────────────────────────────────────────────┐
│                    LinguaLearn Monolith                │
│                                                        │
│  ┌──────────────┐  ┌──────────────┐  ┌─────────────┐ │
│  │ UserModule   │  │ LessonModule │  │ VideoModule │ │
│  └──────────────┘  └──────────────┘  └─────────────┘ │
│  ┌──────────────┐  ┌──────────────┐  ┌─────────────┐ │
│  │ProgressModule│  │ NotifyModule │  │ AdminModule │ │
│  └──────────────┘  └──────────────┘  └─────────────┘ │
│                                                        │
│  ┌────────────────────────────────────────────────┐   │
│  │           Single Shared Database               │   │
│  └────────────────────────────────────────────────┘   │
└───────────────────────────────────────────────────────┘
```

**✅ Monolith Advantages:**
- Simple to develop: one codebase, one IDE, one debugger
- Simple to deploy: one build, one container
- Transactions are easy: ACID transactions across all data
- No network calls between modules: function calls are instant
- Easy to debug: all logs in one place, one stack trace
- Faster to iterate early: less infrastructure overhead

**❌ Monolith Problems (at scale):**
- **Deployment coupling:** All teams must deploy together
- **Scaling coupling:** Can't scale one feature without scaling all
- **Technology lock-in:** Entire app must use the same language and framework
- **Codebase size:** 500K+ line codebases are hard to understand
- **Team ownership:** Nobody truly owns anything; everyone touches everything
- **Blast radius:** A bug in one module can crash the entire application

### Microservices

Each feature area becomes an independent service with its own codebase, database, and deployment pipeline.

```
                    [API Gateway]
                    /     |      \
   [UserService] [LessonService] [VideoService]
         |              |               |
   [Users DB]    [Lessons DB]    [Videos DB]
   
   [ProgressService]  [NotifyService]
         |                  |
   [Progress DB]      [Email/SMS Queue]
```

**✅ Microservices Advantages:**
- **Independent deployment:** The Video team deploys on Tuesday; the Quiz team deploys on Thursday. No coordination needed.
- **Independent scaling:** Scale the Video Service to 20 instances on exam day. Keep the Admin Service at 2 instances.
- **Technology flexibility:** UserService in C#, VideoService in Go (better for streaming), AnalyticsService in Python (better for data science).
- **Team ownership:** Each team owns their service end-to-end — code, database, deployment, monitoring.
- **Fault isolation:** The Notification Service going down doesn't crash the Lesson Service.
- **Smaller codebases:** Each service is small enough for any team member to fully understand.

**❌ Microservices Costs:**
- **Distributed system complexity:** Every call between services is a network call (see Chapter 1).
- **Operational overhead:** 10 services = 10 deployment pipelines, 10 databases, 10 log streams.
- **Distributed transactions:** ACID transactions don't work across services. You need Sagas (Section 9).
- **Data consistency:** Enforcing referential integrity across service databases is complex.
- **Testing complexity:** Integration tests must spin up multiple services.
- **Latency:** A service-to-service call adds network latency on top of business logic time.

> **The Rule:** Start with a monolith. Break it into microservices only when you have a concrete problem that microservices solve — most commonly: deployment coupling, scaling coupling, or team ownership issues. Microservices are a solution to an organizational scale problem. They are not inherently better architecture.

---

## 3. Domain-Driven Design — Finding the Boundaries

The hardest part of microservices is not building them — it's figuring out where to draw the boundaries. Cut the boundaries wrong and you end up with services that are constantly calling each other, creating a **distributed monolith** that has all the costs of microservices with none of the benefits.

**Domain-Driven Design (DDD)** gives us a principled approach to finding those boundaries through the concept of **Bounded Contexts**.

### What Is a Bounded Context?

A Bounded Context is a boundary within which a particular domain model applies. Inside the boundary, terms have specific, consistent meanings. Outside the boundary, the same word might mean something completely different.

Here's a surprising example from LinguaLearn:

The word **"User"** means something different in each context:

| Service Context | "User" means... |
|----------------|-----------------|
| Identity/Auth | A set of credentials (email, password hash, roles) |
| Lesson Context | A student with a learning level, enrolled lessons, and progress |
| Video Context | A viewer with streaming preferences and watch history |
| Billing Context | A subscriber with a payment method and subscription tier |
| Notification Context | A recipient with contact preferences (email/SMS/push) |

If you have one giant `User` class that tries to model all of these simultaneously, it becomes a massive, incoherent blob with 50 properties, most of which are irrelevant in any given context.

A **Bounded Context** separates these. The Identity service has its own lean `User` model. The Lesson service has its own `Student` model (which refers to the same person but only cares about lesson-relevant data). They are **the same person in the real world but modeled differently in each context.**

### Identifying Bounded Contexts

To find your bounded contexts, look for:
1. **Differences in language** — When two teams use the same word to mean different things
2. **Different rates of change** — Billing data changes for business reasons; video streaming logic changes for technical reasons
3. **Different teams** — If a different team owns it, it's likely a different bounded context
4. **Cohesion within, minimal coupling between** — Everything inside the boundary is closely related; things outside are referenced only by ID

---

## 4. Designing the LinguaLearn Services

Applying DDD, here are the bounded contexts (and therefore microservices) for LinguaLearn:

```
┌─────────────────────────────────────────────────────────────────────┐
│                    LinguaLearn Service Map                           │
│                                                                      │
│  ┌───────────────────┐                                              │
│  │   IdentityService │ ← Authentication, authorization, JWT tokens  │
│  │   (C#, PostgreSQL)│   User credentials, roles, permissions       │
│  └───────────────────┘                                              │
│                                                                      │
│  ┌───────────────────┐                                              │
│  │   SchoolService   │ ← Tenant management, school settings         │
│  │   (C#, PostgreSQL)│   Branding, features, subscription plans     │
│  └───────────────────┘                                              │
│                                                                      │
│  ┌───────────────────┐                                              │
│  │   LessonService   │ ← Lesson catalog, curriculum, quizzes        │
│  │   (C#, PostgreSQL)│   Teacher-created content                    │
│  └───────────────────┘                                              │
│                                                                      │
│  ┌───────────────────┐                                              │
│  │   VideoService    │ ← Video storage, transcoding, streaming      │
│  │   (Go, S3/CDN)    │   Watch history, progress tracking           │
│  └───────────────────┘                                              │
│                                                                      │
│  ┌───────────────────┐                                              │
│  │  ProgressService  │ ← Student progress, quiz scores, certificates│
│  │   (C#, PostgreSQL)│   Learning analytics, completion records     │
│  └───────────────────┘                                              │
│                                                                      │
│  ┌───────────────────┐                                              │
│  │ NotificationService│ ← Email, push notifications, SMS            │
│  │   (C#, Redis)     │   Template management, delivery tracking     │
│  └───────────────────┘                                              │
│                                                                      │
│  ┌───────────────────┐                                              │
│  │  AnalyticsService │ ← Platform-wide reports, school dashboards   │
│  │  (Python, OLAP DB)│   Data aggregation, trend analysis           │
│  └───────────────────┘                                              │
│                                                                      │
│  ┌───────────────────┐                                              │
│  │    API Gateway    │ ← Single entry point for all client calls    │
│  │   (Nginx/YARP)    │   Routing, auth validation, rate limiting    │
│  └───────────────────┘                                              │
└─────────────────────────────────────────────────────────────────────┘
```

Each service:
- Has **its own database** — complete data ownership
- Has **its own Git repository** — independent codebase
- Has **its own deployment pipeline** — deploy independently
- Is **owned by one team** — clear responsibility

---

## 5. Service Communication — Sync vs. Async

Services need to talk to each other. The most important architecture decision in microservices is **how** services communicate.

### Synchronous Communication (Request/Response)

Service A sends a request to Service B and **waits** for a response before continuing.

```
LessonService → [HTTP GET] → VideoService
LessonService ← [Response: VideoMetadata] ← VideoService
```

**When to use:** When the caller needs the response immediately to continue its work.

**Example:** "Get the video metadata for Lesson 12 so I can include it in the lesson page response."

**Risks:** 
- If VideoService is down → LessonService call fails
- Long chains of synchronous calls increase total latency multiplicatively
- Creates **temporal coupling** — services must be up simultaneously

### Asynchronous Communication (Events)

Service A publishes an event to a message bus and **doesn't wait** for a response. Zero or more services consume that event.

```
ProgressService → [publish: StudentPassedQuiz event] → Message Bus
                              ↓ (asynchronous)
               ├─► NotificationService: send congratulations email
               ├─► CertificateService: generate certificate PDF
               └─► AnalyticsService: update completion statistics
```

**When to use:** When the action has side effects that don't need to happen immediately.

**Example:** "A student passed a quiz. Eventually, they should get an email. We don't need to wait for the email to send before telling the student they passed."

**Benefits:**
- Decoupling — publishers don't know who consumes their events
- Resilience — consumers can be down; events queue up and are processed when they recover
- Scalability — add new consumers without changing the publisher

### The Decision Matrix

```
                 Does the caller need the response immediately?
                              │
                    ┌─────────┴──────────┐
                   YES                  NO
                    │                    │
            Synchronous              Asynchronous
           (HTTP / gRPC)            (Events / Queue)
                    │                    │
          Response in < 500ms    Side effects: email, 
          User waiting           analytics, cache updates,
          for the result         audit logs, certificates
```

---

## 6. Building a Minimal API Service in C#

Each microservice is a self-contained .NET application. .NET's **Minimal API** style is perfect for microservices — it's lightweight, fast to start up, and has minimal boilerplate.

```csharp
// ─────────────────────────────────────────────────────────────────
// LessonService/Program.cs — A complete microservice entry point
// ─────────────────────────────────────────────────────────────────
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Structured logging with Serilog (essential for microservices observability)
builder.Host.UseSerilog((context, config) =>
    config.ReadFrom.Configuration(context.Configuration)
          .Enrich.WithProperty("Service", "LessonService")
          .Enrich.WithProperty("Environment", context.HostingEnvironment.EnvironmentName)
          .WriteTo.Console()
          .WriteTo.Seq("http://seq.monitoring.internal")); // Centralized log aggregator

// Database
builder.Services.AddDbContext<LessonDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("LessonDatabase")));

// Tenant support (see Chapter 2)
builder.Services.AddScoped<ITenantContext, TenantContext>();

// Application services
builder.Services.AddScoped<ILessonService, LessonService>();
builder.Services.AddScoped<ILessonRepository, LessonRepository>();

// HTTP client for calling Video Service (with Polly resilience)
builder.Services.AddHttpClient<IVideoServiceClient, VideoServiceClient>(client =>
    client.BaseAddress = new Uri(builder.Configuration["ServiceUrls:VideoService"]!))
    .AddPolicyHandler(ResiliencePolicies.GetRetryPolicy())
    .AddPolicyHandler(ResiliencePolicies.GetCircuitBreakerPolicy());

// Health checks
builder.Services.AddHealthChecks()
    .AddDbContextCheck<LessonDbContext>("lesson-db")
    .AddCheck("self", () => HealthCheckResult.Healthy());

// Distributed cache
builder.Services.AddStackExchangeRedisCache(options =>
    options.Configuration = builder.Configuration.GetConnectionString("Redis"));

// Rate limiting
builder.Services.AddRateLimiter(options =>
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
        context => RateLimitPartition.GetFixedWindowLimiter(
            context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "anon",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 200, Window = TimeSpan.FromSeconds(1) })));

// OpenTelemetry for distributed tracing (covered in Section 11)
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddEntityFrameworkCoreInstrumentation()
        .AddOtlpExporter()); // Sends traces to Jaeger/Tempo

var app = builder.Build();

app.UseMiddleware<TenantResolutionMiddleware>();
app.UseRateLimiter();

// ─────────────────────────────────────────────────────────────────
// Lesson Endpoints — Minimal API style
// ─────────────────────────────────────────────────────────────────

var lessons = app.MapGroup("/api/lessons").RequireAuthorization();

lessons.MapGet("/", async (ILessonService service) =>
    Results.Ok(await service.GetAllAsync()));

lessons.MapGet("/{id:int}", async (int id, ILessonService service) =>
{
    var lesson = await service.GetByIdAsync(id);
    return lesson is null ? Results.NotFound() : Results.Ok(lesson);
});

lessons.MapPost("/", async (CreateLessonRequest request, ILessonService service) =>
{
    var lesson = await service.CreateAsync(request);
    return Results.CreatedAtRoute("GetLesson", new { lesson.Id }, lesson);
});

lessons.MapPut("/{id:int}", async (int id, UpdateLessonRequest request, ILessonService service) =>
{
    await service.UpdateAsync(id, request);
    return Results.NoContent();
});

lessons.MapDelete("/{id:int}", async (int id, ILessonService service) =>
{
    await service.DeleteAsync(id);
    return Results.NoContent();
});

// Health check endpoints for load balancer
app.MapHealthChecks("/health");

app.Run();
```

---

## 7. The API Gateway — The Front Door

In a microservices architecture, clients should not call individual services directly. Why? Because:

1. **Each service has a different URL** — clients would need to know all service addresses
2. **Auth should be centralized** — validating JWT tokens in every service is wasteful
3. **Rate limiting** — better to apply it once at the gateway than in every service
4. **SSL termination** — handle HTTPS once at the gateway; internal traffic can be plain HTTP
5. **Request routing** — the gateway decides which service handles each request

The **API Gateway** is a single entry point that proxies all client requests to the appropriate backend service.

```
                        Client (Mobile App, Browser)
                                  │
                       HTTPS (443) Request
                                  │
                          ┌───────▼────────┐
                          │   API Gateway  │
                          │  (YARP/Nginx)  │
                          │  ─────────── │
                          │  - JWT Auth   │
                          │  - Rate Limit │
                          │  - Routing    │
                          │  - SSL Term.  │
                          └───────┬────────┘
              ┌───────────────────┼────────────────────┐
              │                   │                    │
    GET /api/lessons   POST /api/quiz   GET /api/videos
              │                   │                    │
      [LessonService]    [ProgressService]    [VideoService]
```

```csharp
// ─────────────────────────────────────────────────────────────────
// ApiGateway/Program.cs — YARP Reverse Proxy as API Gateway
// Install: dotnet add package Yarp.ReverseProxy
// ─────────────────────────────────────────────────────────────────
var builder = WebApplication.CreateBuilder(args);

// Configure YARP reverse proxy
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

// JWT authentication (validate tokens once at the gateway)
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = builder.Configuration["Auth:Authority"]; // Identity Service URL
        options.Audience = "lingualearn-api";
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true
        };
    });

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapReverseProxy(); // YARP handles routing based on config
app.Run();
```

```json
// appsettings.json — YARP routing configuration
{
  "ReverseProxy": {
    "Routes": {
      "lessons-route": {
        "ClusterId": "lesson-service",
        "AuthorizationPolicy": "default",
        "Match": { "Path": "/api/lessons/{**catch-all}" }
      },
      "progress-route": {
        "ClusterId": "progress-service",
        "AuthorizationPolicy": "default",
        "Match": { "Path": "/api/progress/{**catch-all}" }
      },
      "video-route": {
        "ClusterId": "video-service",
        "AuthorizationPolicy": "default",
        "Match": { "Path": "/api/videos/{**catch-all}" }
      },
      "public-route": {
        "ClusterId": "identity-service",
        "Match": { "Path": "/api/auth/{**catch-all}" }
      }
    },
    "Clusters": {
      "lesson-service": {
        "Destinations": {
          "lesson-service-1": { "Address": "http://lesson-service:8080" }
        }
      },
      "progress-service": {
        "Destinations": {
          "progress-service-1": { "Address": "http://progress-service:8080" }
        }
      },
      "video-service": {
        "Destinations": {
          "video-service-1": { "Address": "http://video-service:8080" }
        }
      }
    }
  }
}
```

---

## 8. The Event Bus — Async Communication with MassTransit

The event bus is the nervous system of a microservices architecture. It allows services to communicate without knowing about each other.

### Publishing Events

```csharp
// ─────────────────────────────────────────────────────────────────
// Shared contracts library: LinguaLearn.Contracts
// Events are shared type definitions used by publishers and consumers
// ─────────────────────────────────────────────────────────────────

// LinguaLearn.Contracts/Events/StudentEvents.cs
namespace LinguaLearn.Contracts.Events;

// Published by: ProgressService
// Consumed by: NotificationService, CertificateService, AnalyticsService
public record StudentCompletedLessonEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
    public string TenantId { get; init; } = string.Empty;
    public int StudentId { get; init; }
    public int LessonId { get; init; }
    public int ScorePercent { get; init; }
    public bool Passed { get; init; }
    public int TotalLessonsCompleted { get; init; }
}

// Published by: IdentityService when a new student registers
public record StudentRegisteredEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
    public string TenantId { get; init; } = string.Empty;
    public int StudentId { get; init; }
    public string StudentName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
}
```

```csharp
// ─────────────────────────────────────────────────────────────────
// ProgressService — Publishing an event when a student completes a lesson
// ─────────────────────────────────────────────────────────────────
public class QuizCompletionService : IQuizCompletionService
{
    private readonly IProgressRepository _repository;
    private readonly IPublishEndpoint _bus;  // MassTransit publish endpoint
    private readonly ITenantContext _tenant;

    public QuizCompletionService(
        IProgressRepository repository,
        IPublishEndpoint bus,
        ITenantContext tenant)
    {
        _repository = repository;
        _bus = bus;
        _tenant = tenant;
    }

    public async Task<QuizResult> RecordCompletionAsync(int studentId, int lessonId, int scorePercent)
    {
        var passed = scorePercent >= 70; // Passing score is 70%

        // Save to our own database
        var progress = new LessonProgress
        {
            StudentId = studentId,
            LessonId = lessonId,
            ScorePercent = scorePercent,
            IsCompleted = passed,
            CompletedAt = DateTime.UtcNow
        };
        await _repository.SaveProgressAsync(progress);

        var totalCompleted = await _repository.GetCompletedCountAsync(studentId);

        // Publish event — other services will react to this
        // ProgressService doesn't care WHO reacts or HOW MANY react
        await _bus.Publish(new StudentCompletedLessonEvent
        {
            TenantId = _tenant.TenantId,
            StudentId = studentId,
            LessonId = lessonId,
            ScorePercent = scorePercent,
            Passed = passed,
            TotalLessonsCompleted = totalCompleted
        });

        return new QuizResult { Passed = passed, Score = scorePercent };
    }
}
```

```csharp
// ─────────────────────────────────────────────────────────────────
// NotificationService — Consuming the event
// ─────────────────────────────────────────────────────────────────
public class LessonCompletedEmailConsumer : IConsumer<StudentCompletedLessonEvent>
{
    private readonly IEmailTemplateService _emailService;
    private readonly IStudentQueryService _studentQuery;
    private readonly ILogger<LessonCompletedEmailConsumer> _logger;

    public async Task Consume(ConsumeContext<StudentCompletedLessonEvent> context)
    {
        var ev = context.Message;
        
        // Only send email if the student passed
        if (!ev.Passed)
        {
            _logger.LogDebug("Student {StudentId} did not pass. Skipping notification.", ev.StudentId);
            return;
        }

        // Query the Identity Service to get the student's email
        // (NotificationService doesn't store student emails in its own DB)
        var student = await _studentQuery.GetStudentContactInfoAsync(ev.StudentId);
        if (student is null) return;

        await _emailService.SendTemplatedEmailAsync(
            to: student.Email,
            templateName: "lesson_completed",
            data: new
            {
                StudentName = student.Name,
                LessonId = ev.LessonId,
                Score = ev.ScorePercent,
                TotalCompleted = ev.TotalLessonsCompleted,
                // Show special message if they hit milestone completions
                IsMilestone = ev.TotalLessonsCompleted % 10 == 0
            }
        );

        _logger.LogInformation("Sent lesson completion email to {Email} for lesson {LessonId}",
            student.Email, ev.LessonId);
    }
}
```

---

## 9. The Saga Pattern — Distributed Transactions

Here's a problem that trips up everyone new to microservices: **ACID transactions don't work across service boundaries.**

In a monolith with one database, if you want to enroll a student in a school (which involves creating a user account, assigning a school license, initializing progress records, and sending a welcome email), you wrap it all in a database transaction. If any step fails, everything rolls back.

In microservices, these steps span four different services, each with their own database. There is no distributed transaction manager (and even if there were, it would be a catastrophic performance bottleneck).

The solution is the **Saga pattern** — a sequence of local transactions coordinated by events or commands. If a step fails, **compensating transactions** undo the previous steps.

### Choreography Saga (Event-Driven)

Each service listens for an event and publishes the next event in response. No central coordinator.

```
Student Registration Saga (Choreography):

1. IdentityService creates user account
   → publishes: UserAccountCreated

2. SchoolService hears UserAccountCreated
   → assigns school license, initializes student settings
   → publishes: StudentEnrolled (success)
   → OR publishes: StudentEnrollmentFailed (if no licenses available)

3. ProgressService hears StudentEnrolled
   → initializes progress records (first lesson unlocked)
   → publishes: ProgressInitialized

4. NotificationService hears ProgressInitialized
   → sends welcome email
   → publishes: WelcomeEmailSent

If ANY step fails:
   SchoolService publishes: StudentEnrollmentFailed
   IdentityService hears it → deletes the user account (compensating transaction)
   → Student sees: "Enrollment failed. Please try again."
```

### Orchestration Saga (Central Coordinator)

A dedicated Saga Orchestrator service directs each step and handles failures. More explicit control flow.

```csharp
// ─────────────────────────────────────────────────────────────────
// StudentEnrollmentSaga.cs — MassTransit Saga with State Machine
// Install: dotnet add package MassTransit
// ─────────────────────────────────────────────────────────────────
public class StudentEnrollmentState : SagaStateMachineInstance
{
    public Guid CorrelationId { get; set; }
    public string CurrentState { get; set; } = string.Empty;

    // Track saga data across steps
    public string TenantId { get; set; } = string.Empty;
    public int StudentId { get; set; }
    public string StudentEmail { get; set; } = string.Empty;
    public string StudentName { get; set; } = string.Empty;
    public bool UserAccountCreated { get; set; }
    public bool LicenseAssigned { get; set; }
    public bool ProgressInitialized { get; set; }
    public string? FailureReason { get; set; }
}

public class StudentEnrollmentSaga : MassTransitStateMachine<StudentEnrollmentState>
{
    // States in the saga lifecycle
    public State CreatingUser { get; private set; } = null!;
    public State AssigningLicense { get; private set; } = null!;
    public State InitializingProgress { get; private set; } = null!;
    public State SendingWelcomeEmail { get; private set; } = null!;
    public State Completed { get; private set; } = null!;
    public State Failed { get; private set; } = null!;

    // Events that trigger state transitions
    public Event<EnrollStudentCommand> EnrollStudent { get; private set; } = null!;
    public Event<UserAccountCreatedEvent> UserAccountCreated { get; private set; } = null!;
    public Event<LicenseAssignedEvent> LicenseAssigned { get; private set; } = null!;
    public Event<ProgressInitializedEvent> ProgressInitialized { get; private set; } = null!;
    public Event<EnrollmentStepFailedEvent> StepFailed { get; private set; } = null!;

    public StudentEnrollmentSaga()
    {
        InstanceState(x => x.CurrentState);

        // ── Step 1: Receive enrollment command ──────────────────────────
        Initially(
            When(EnrollStudent)
                .Then(context =>
                {
                    context.Saga.TenantId = context.Message.TenantId;
                    context.Saga.StudentEmail = context.Message.Email;
                    context.Saga.StudentName = context.Message.Name;
                    Log.Information("Starting enrollment saga for {Email}", context.Message.Email);
                })
                // Tell IdentityService to create the user account
                .PublishAsync(context => context.Init<CreateUserAccountCommand>(new
                {
                    context.Saga.TenantId,
                    context.Saga.StudentEmail,
                    context.Saga.StudentName
                }))
                .TransitionTo(CreatingUser)
        );

        // ── Step 2: User account created → assign license ───────────────
        During(CreatingUser,
            When(UserAccountCreated)
                .Then(context =>
                {
                    context.Saga.StudentId = context.Message.StudentId;
                    context.Saga.UserAccountCreated = true;
                })
                .PublishAsync(context => context.Init<AssignSchoolLicenseCommand>(new
                {
                    context.Saga.TenantId,
                    context.Saga.StudentId
                }))
                .TransitionTo(AssigningLicense),

            // If this step fails, there's nothing to compensate yet
            When(StepFailed)
                .Then(context => context.Saga.FailureReason = context.Message.Reason)
                .TransitionTo(Failed)
        );

        // ── Step 3: License assigned → initialize progress ──────────────
        During(AssigningLicense,
            When(LicenseAssigned)
                .Then(context => context.Saga.LicenseAssigned = true)
                .PublishAsync(context => context.Init<InitializeStudentProgressCommand>(new
                {
                    context.Saga.TenantId,
                    context.Saga.StudentId
                }))
                .TransitionTo(InitializingProgress),

            // Compensate: license assignment failed → delete the user account
            When(StepFailed)
                .Then(context => context.Saga.FailureReason = context.Message.Reason)
                .PublishAsync(context => context.Init<DeleteUserAccountCommand>(new
                {
                    context.Saga.StudentId  // Roll back Step 1
                }))
                .TransitionTo(Failed)
        );

        // ── Step 4: Progress initialized → send welcome email ───────────
        During(InitializingProgress,
            When(ProgressInitialized)
                .Then(context => context.Saga.ProgressInitialized = true)
                .PublishAsync(context => context.Init<SendWelcomeEmailCommand>(new
                {
                    context.Saga.StudentEmail,
                    context.Saga.StudentName,
                    context.Saga.TenantId
                }))
                .TransitionTo(Completed) // Enrollment is complete; email is best-effort
        );

        // ── Final states ─────────────────────────────────────────────────
        SetCompletedWhenFinalized();
    }
}
```

The Saga pattern guarantees that **either all steps eventually complete, or compensating transactions undo completed steps**. It achieves distributed consistency without the performance penalty of a distributed lock or two-phase commit.

---

## 10. Resilience with Polly — Calling Other Services Safely

When a service calls another service, it must be resilient to failures. We covered Polly in Chapter 1, but let's look at the microservices-specific patterns.

```csharp
// ─────────────────────────────────────────────────────────────────
// ResiliencePolicies.cs — Reusable policies for service-to-service calls
// ─────────────────────────────────────────────────────────────────
public static class ResiliencePolicies
{
    // A combined policy: Retry inside a Circuit Breaker
    // The Polly "PolicyWrap" applies policies from outer to inner
    public static IAsyncPolicy<HttpResponseMessage> GetCombinedPolicy()
    {
        return Policy.WrapAsync(
            GetCircuitBreakerPolicy(),  // Outer: circuit breaker
            GetRetryPolicy(),           // Inner: retry (only retries when circuit is closed)
            GetTimeoutPolicy()          // Innermost: timeout per attempt
        );
    }

    public static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
    {
        var jitter = new Random();
        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .WaitAndRetryAsync(3, retryAttempt =>
                TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)) +
                TimeSpan.FromMilliseconds(jitter.Next(0, 100)));
    }

    public static IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy()
    {
        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .CircuitBreakerAsync(5, TimeSpan.FromSeconds(30));
    }

    public static IAsyncPolicy<HttpResponseMessage> GetTimeoutPolicy()
    {
        // Each individual attempt (before retry) must complete within 5 seconds
        return Policy.TimeoutAsync<HttpResponseMessage>(5);
    }
}
```

---

## 11. Distributed Tracing and Observability

Here's a scenario: A student reports that loading the lesson page takes 8 seconds sometimes. You look at the Lesson Service logs — it shows the request completed in 200ms. So where did the other 7.8 seconds go?

The request touched 4 services: API Gateway → Lesson Service → Video Service → CDN check. The slowness is in the Video Service, but looking at Lesson Service logs, you'd never know that.

**Distributed tracing** gives you a complete timeline of a request as it flows through multiple services.

```
Trace ID: 8f3a-29cd-beef-cafe
────────────────────────────────────────────────────────────────
[API Gateway]          0ms     ─────────────────── 8,200ms
  [Lesson Service]     10ms    ──────────────────── 8,180ms
    [Redis Cache]      12ms    ─ 2ms (MISS)
    [Lesson DB]        15ms    ─── 5ms (query OK)
    [Video Service call] 20ms  ────────────────── 8,160ms  ← HERE!
      [Video DB query]   21ms  ─────────────────── 8,150ms ← Slow query!
    [Response built]   8,165ms ── 15ms
  [Return to gateway]  8,180ms ─ 20ms
────────────────────────────────────────────────────────────────
Total: 8,200ms. Problem identified: Video DB slow query.
```

```csharp
// ─────────────────────────────────────────────────────────────────
// Program.cs — OpenTelemetry distributed tracing setup
// Install: dotnet add package OpenTelemetry.Extensions.Hosting
//          dotnet add package OpenTelemetry.Instrumentation.AspNetCore
//          dotnet add package OpenTelemetry.Instrumentation.HttpClient
//          dotnet add package OpenTelemetry.Exporter.Otlp
// ─────────────────────────────────────────────────────────────────
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing
            // Auto-instrument incoming HTTP requests
            .AddAspNetCoreInstrumentation(options =>
            {
                options.RecordException = true;
                options.Filter = context => !context.Request.Path.StartsWithSegments("/health");
            })
            // Auto-instrument outgoing HTTP calls (to other services)
            .AddHttpClientInstrumentation(options => options.RecordException = true)
            // Auto-instrument EF Core database calls
            .AddEntityFrameworkCoreInstrumentation(options => options.SetDbStatementForText = true)
            // Name this service in the trace
            .AddSource("LessonService")
            // Export traces to Jaeger or Grafana Tempo
            .AddOtlpExporter(options =>
                options.Endpoint = new Uri(builder.Configuration["Telemetry:OtlpEndpoint"]!));
    });
```

```csharp
// ─────────────────────────────────────────────────────────────────
// Adding custom spans to trace business-level operations
// ─────────────────────────────────────────────────────────────────
public class LessonService : ILessonService
{
    private static readonly ActivitySource ActivitySource = new("LessonService");
    private readonly ILessonRepository _repository;
    private readonly IVideoServiceClient _videoClient;

    public async Task<LessonDetailResponse?> GetLessonWithVideoAsync(int lessonId)
    {
        // Create a custom span for this operation
        using var activity = ActivitySource.StartActivity("GetLessonWithVideo");
        activity?.SetTag("lesson.id", lessonId);

        var lesson = await _repository.GetByIdAsync(lessonId);
        if (lesson is null) return null;

        using var videoActivity = ActivitySource.StartActivity("FetchVideoMetadata");
        videoActivity?.SetTag("video.id", lesson.VideoId);

        var videoMetadata = await _videoClient.GetVideoMetadataAsync(lesson.VideoId);

        videoActivity?.SetTag("video.duration_seconds", videoMetadata?.DurationSeconds);
        activity?.SetTag("lesson.unit", lesson.Unit);
        activity?.SetTag("lesson.level", lesson.Level);

        return new LessonDetailResponse
        {
            Lesson = lesson,
            VideoMetadata = videoMetadata
        };
    }
}
```

The propagation of trace context across service calls is handled automatically by OpenTelemetry. When Lesson Service calls Video Service via `HttpClient`, it injects a `traceparent` header into the request. Video Service reads this header and continues the same trace. All spans from all services are stitched together by the trace ID.

---

## 12. Service Mesh — Managing Service-to-Service Traffic

As the number of microservices grows, managing service-to-service traffic becomes complex:
- How do you enforce mTLS (mutual TLS) between every pair of services?
- How do you apply rate limiting at the service-to-service level?
- How do you route traffic for canary deployments (send 10% of traffic to the new version)?

A **service mesh** handles all of this transparently, without changing your application code. It deploys a lightweight **sidecar proxy** (usually Envoy) alongside every service. All traffic in and out of a service goes through the sidecar.

```
┌─────────────────────────────────────────────────────────────────┐
│   Service Mesh (e.g., Istio, Linkerd)                           │
│                                                                   │
│  ┌──────────────────┐         ┌──────────────────┐             │
│  │  LessonService   │         │  VideoService     │             │
│  │  ┌────────────┐  │  mTLS   │  ┌────────────┐  │             │
│  │  │  App Code  │  │◄───────►│  │  App Code  │  │             │
│  │  └────────────┘  │         │  └────────────┘  │             │
│  │  ┌────────────┐  │         │  ┌────────────┐  │             │
│  │  │   Envoy    │  │         │  │   Envoy    │  │             │
│  │  │  (sidecar) │  │         │  │  (sidecar) │  │             │
│  │  └────────────┘  │         │  └────────────┘  │             │
│  └──────────────────┘         └──────────────────┘             │
│                                                                   │
│  Control Plane (Istiod):                                         │
│  - Configures all sidecars centrally                             │
│  - Issues mTLS certificates                                      │
│  - Enforces traffic policies                                     │
│  - Collects telemetry from all sidecars                          │
└─────────────────────────────────────────────────────────────────┘
```

Service meshes are appropriate when you have 10+ microservices and need centralized traffic management. For smaller deployments, Polly + YARP is sufficient.

---

## 13. The Education Platform Scenario — Student Enrollment

Let's trace the complete enrollment flow through LinguaLearn's microservices:

```
A new school signs up. Their administrator creates the first batch of student accounts.

1. Admin uploads a CSV with 500 student emails via the School Admin Portal.

2. API Gateway receives the request:
   - Validates the JWT token (admin role confirmed)
   - Routes to SchoolService: POST /api/schools/{tenantId}/students/bulk-enroll

3. SchoolService:
   - Validates the CSV format
   - For each student, publishes a "EnrollStudent" command to the message bus
   - Returns immediately: "500 enrollments queued. Check dashboard for progress."

4. The StudentEnrollmentSaga begins for each student (running in parallel):

   Step A → IdentityService: Create user account
            → Publishes: UserAccountCreated (StudentId: 1001)

   Step B → SchoolService: Assigns one of the school's 500 purchased licenses
            → Publishes: LicenseAssigned (StudentId: 1001)

   Step C → ProgressService: Creates initial progress record
            → First lesson unlocked automatically
            → Publishes: ProgressInitialized (StudentId: 1001)

   Step D → NotificationService: Sends welcome email
            → Uses templated email with school branding
            → Student receives: "Welcome to Tokyo English Academy!"

5. If ANY step fails (e.g., the school ran out of licenses):
   - Saga compensates: deletes the user account
   - Admin dashboard shows: "Student emma@example.com: Enrollment failed - No licenses remaining"

6. After all 500 students processed (5-10 minutes later):
   - Admin sees: "498 enrolled successfully, 2 failed (insufficient licenses)"
   - All 498 students can now log in immediately

The admin never waits. The system handles 500 concurrent enrollments reliably.
Each service did its job independently. The Saga ensured data consistency without 
a distributed transaction.
```

---

## 14. The Microservices Trap — When NOT to Use Them

This section might be the most important in this chapter. The technology industry goes through hype cycles, and microservices had a massive hype peak in the 2015-2020 period. Many companies adopted them without genuinely needing them, and paid a steep price.

### Signs You're Not Ready for Microservices

**"We have 3 developers."**
Microservices are an organizational scaling solution. Three developers cannot own, maintain, and operate 7 different services effectively. Each service needs its own CI/CD, monitoring, and alerting. Three developers will be overwhelmed by operations before they finish any features.

**"Our monolith is slow and messy."**
A messy monolith usually means messy code — which becomes messy microservices. The boundary problems, the coupling issues — they follow you into microservices if you haven't understood the domain well. First, clean up the monolith's internal architecture. Only then consider extracting services.

**"We heard Netflix uses microservices."**
Netflix has 2,000+ engineers and processes 15+ billion requests per day. At that scale, microservices are the only viable option. At your scale, they are likely premature. As the saying goes: "Don't scale your architecture to Netflix's problems until you have Netflix's problems."

**"We want to use different technologies."**
This is a legitimate reason — but be honest about whether you actually need different technologies now or just think you might someday.

### The Strangler Fig Pattern — Migrating Safely

If you have a monolith that needs to become microservices, the **Strangler Fig Pattern** is the safe way to do it. Named after a fig tree that grows around a host tree and gradually replaces it.

```
Phase 1: Monolith handles everything
  Client → Monolith → Database

Phase 2: Extract one service (e.g., VideoService)
  Client → API Gateway → VideoService (new)
                      → Monolith (everything else)
  
Phase 3: Extract another service (e.g., NotificationService)
  Client → API Gateway → VideoService (new)
                      → NotificationService (new)
                      → Monolith (shrinking)

Phase 4: Continue until the monolith is gone
  Client → API Gateway → Service A
                      → Service B
                      → Service C (no more monolith)
```

Never try to rewrite everything from scratch. Extract one service at a time. Prove it works. Then extract the next one.

---

## 15. Decision Guide and Migration Path

### Should You Break Out a Microservice?

Ask these questions about the component you're considering:

1. **Does a different team own this?** If yes → strong case for extraction
2. **Does it scale differently from the rest?** If yes → moderate case
3. **Does it deploy independently today?** If no → extraction adds value
4. **Does it use a fundamentally different technology?** If yes → might justify extraction
5. **Is it less than 6 months old?** If yes → wait. You don't fully understand it yet.

### Recommended Migration Order for LinguaLearn

| Phase | Services to Extract | Why |
|-------|---------------------|-----|
| Phase 1 | NotificationService | Fewest dependencies. Clean boundary. |
| Phase 2 | VideoService | Different scaling profile. Potentially different tech. |
| Phase 3 | ProgressService | Clear ownership by one team. |
| Phase 4 | LessonService | High traffic. Team wants independent deployment. |
| Phase 5 | IdentityService | Complex but worth it. Auth is foundational. |
| Last | AnalyticsService | High effort. Do it when data team is established. |

---

## 16. Summary and Key Takeaways

### Core Concepts

| Concept | One-Line Summary |
|---------|-----------------|
| Microservice | A small, independently deployable service owning one business domain |
| Monolith | A single deployable unit. Start here. Migrate when there's a real reason. |
| Bounded Context | A DDD concept that defines a natural service boundary |
| API Gateway | The single entry point that routes, authenticates, and rate-limits all client requests |
| Synchronous Communication | HTTP/gRPC calls where the caller waits for a response |
| Asynchronous Communication | Event publishing where the caller doesn't wait for consumers |
| Saga Pattern | Multi-step distributed process with compensating transactions on failure |
| Distributed Tracing | Following a request's journey across multiple services via trace IDs |
| Service Mesh | Infrastructure layer that manages service-to-service traffic transparently |
| Strangler Fig | Safe pattern for migrating a monolith to microservices, one service at a time |

### The Microservices Maturity Ladder

```
Level 0: Monolith
  └─► Build this first. Get your domain knowledge right.

Level 1: Modular Monolith
  └─► Clear modules, clean boundaries, shared DB.
      Still one deployment. Much easier to understand.

Level 2: Database-per-Feature (inside one deployment)
  └─► Separate schemas or databases per module.
      Enforce data ownership before splitting the app.

Level 3: First Microservice Extracted
  └─► One service extracted (usually Notifications).
      Everything else still in the monolith.

Level 4: Core Services Split
  └─► 3-5 services. Teams own their services.
      Event bus in place. API Gateway working.

Level 5: Full Microservices + Platform Engineering
  └─► Kubernetes, service mesh, distributed tracing,
      self-service developer platform.
      Requires 20+ engineers to operate sustainably.
```

### The Three Questions to Ask Before Adding a Service

1. **What specific problem does extracting this solve?**
2. **Is the team ready to operate another service (CI/CD, monitoring, on-call)?**
3. **What are the tradeoffs, and are we willing to accept them?**

If you can't answer all three clearly, keep it in the monolith for now.

---

*Congratulations on completing the System Architecture Mastery book! You have learned:*

- *How distributed systems work and why they are challenging*
- *How to serve hundreds of schools securely from one codebase with multi-tenancy*
- *How to handle massive traffic spikes without going down*
- *How to organize complex systems as collaborating microservices*

*These are the skills that distinguish senior engineers from junior ones. The next step is to build something with them.*

*→ Return to [Book Index](./book_INDEX.md)*

---

*Chapter 4 Complete · 16 sections · Microservices Architecture*
*System Architecture Mastery — Complete*
