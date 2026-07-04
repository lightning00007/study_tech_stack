# Chapter 4: Microservices & MediatR

> **System Design · MediatR CQRS · AWS Service Architecture**
> *"MediatR is not just a library — it is a discipline. It forces you to think of every operation as a named, explicit thing with a clear purpose."*
> *"Conway's Law: Any organization that designs a system will produce a design whose structure copies the organization's communication structure."*
> — Melvin Conway, 1967

---

## Table of Contents

1. [Introduction — From Monolith to Service-Oriented Grapeseed](#1-introduction)
2. [Monolith vs. Microservices — An Honest Comparison](#2-monolith-vs-microservices)
3. [MediatR — The Architecture Inside Each Service](#3-mediatr-inside-each-service)
4. [MediatR Commands, Queries, and Notifications](#4-mediatr-cqrs)
5. [MediatR Pipeline Behaviors — The Full Stack](#5-pipeline-behaviors)
6. [Domain-Driven Design — Finding Service Boundaries](#6-domain-driven-design)
7. [Designing Grapeseed's Services](#7-grapeseed-services)
8. [Service Communication — Sync vs. Async](#8-service-communication)
9. [AWS API Gateway — The Front Door](#9-aws-api-gateway)
10. [Amazon SQS/SNS — Async Event Bus Between Services](#10-sqs-sns-event-bus)
11. [The Saga Pattern — Distributed Transactions](#11-saga-pattern)
12. [Resilience with Polly Between Services](#12-resilience-with-polly)
13. [Distributed Tracing with AWS X-Ray](#13-distributed-tracing)
14. [The Grapeseed Scenario — Student Enrollment](#14-grapeseed-scenario)
15. [When NOT to Use Microservices](#15-when-not-to-use)
16. [Summary and Key Takeaways](#16-summary)

---

## 1. Introduction — From Monolith to Service-Oriented Grapeseed

Grapeseed started as a single C# application: one ASP.NET Core project, one RDS PostgreSQL database, deployed on a single ECS task. At the time, this was the right choice. The team was small, features were being discovered, and moving fast mattered more than perfect architecture.

Three years and many schools later, the codebase has grown. The lesson team, the video team, the reporting team, and the school administration team all push code to the same repository. Here's what a typical deployment week looks like:

- **Team A (Lesson Content)** finishes a new unit pacing feature — ready to release Tuesday.
- **Team B (Video Streaming)** is fixing a critical bug in video transcoding — not ready.
- **Team C (Analytics)** pushed a breaking change to a shared model. Team A and Team B just found out.
- **Nobody can release until all three issues are resolved.**

Meanwhile, the video streaming code needs 8 ECS task instances during peak viewing hours. The analytics code runs heavy reports once a week. Yet both are in the same ECS service — you scale both when you only need to scale one, wasting money.

**Microservices solve this.** But before diving in, we need to cover the architectural pattern that keeps each microservice's code clean and organized: **MediatR**.

---

## 2. Monolith vs. Microservices — An Honest Comparison

### The Monolith

```
┌─────────────────────────────────────────────────────┐
│             Grapeseed Monolith (ECS Task)           │
│                                                      │
│  ┌────────────┐  ┌────────────┐  ┌───────────────┐ │
│  │  Identity  │  │  Lessons   │  │    Videos     │ │
│  │  Module    │  │  Module    │  │    Module     │ │
│  └────────────┘  └────────────┘  └───────────────┘ │
│  ┌────────────┐  ┌────────────┐  ┌───────────────┐ │
│  │  Progress  │  │ Analytics  │  │ Notifications │ │
│  │  Module    │  │  Module    │  │    Module     │ │
│  └────────────┘  └────────────┘  └───────────────┘ │
│                                                      │
│         One Shared RDS PostgreSQL Database           │
└─────────────────────────────────────────────────────┘
```

**✅ Monolith Advantages:**
- One codebase — easy to debug, one log stream in CloudWatch
- No network calls between modules — function calls, no latency
- Database transactions work across all modules — ACID guarantees
- Simple deployment — one ECS service to update
- Fast for early-stage development — less infrastructure overhead

**❌ Monolith Problems at Grapeseed's Scale:**
- Deployment coupling — Team A can't release until Team C fixes their bug
- Scaling coupling — can't scale video streaming without scaling analytics
- Blast radius — a bug in analytics crashes the lesson delivery module
- Codebase complexity — 500K+ lines that everyone must understand

### Microservices

```
                   [AWS API Gateway]
                  /        |         \
[IdentityService] [LessonService] [VideoService]
       |                |                |
[RDS PostgreSQL] [RDS PostgreSQL] [S3 + CloudFront]
       
[ProgressService]  [NotificationService]  [AnalyticsService]
       |                  |                      |
[RDS PostgreSQL]    [SQS + SES/SNS]      [RDS SQL Server]
```

**✅ Microservices Advantages:**
- **Independent deployment** — Video team deploys Thursday, Lesson team deploys Friday. No coordination.
- **Independent scaling** — LessonService scales to 30 tasks on exam day. AnalyticsService stays at 2.
- **Technology flexibility** — AnalyticsService uses SQL Server because the analytics team knows T-SQL.
- **Team ownership** — Each team owns their service, their database, their deployment pipeline.
- **Fault isolation** — AnalyticsService down doesn't affect lesson delivery.

**❌ Microservices Costs:**
- Distributed system complexity (network calls between services can fail)
- No cross-service ACID transactions (need Saga pattern)
- Multiple CloudWatch log groups to correlate
- More AWS resources to manage (multiple ECS services, RDS instances, IAM roles)
- Integration testing complexity

> **The Rule:** Start with a modular monolith. Extract microservices only when you have concrete problems (deployment coupling, scaling coupling, team ownership) that microservices solve.

---

## 3. MediatR — The Architecture Inside Each Service

**MediatR** is an in-process mediator library for .NET. It implements the **Mediator pattern**: instead of objects calling each other directly, they send messages through a central mediator, which routes them to the appropriate handler.

In Grapeseed, MediatR is used **within each microservice** to implement CQRS (Command Query Responsibility Segregation) — separating read operations (Queries) from write operations (Commands).

### Why MediatR Changes Everything

Without MediatR, a typical controller looks like this — everything direct-coupled:

```csharp
// ❌ WITHOUT MediatR: Controller knows about every service it needs
[ApiController]
public class ProgressController : ControllerBase
{
    private readonly IProgressRepository _progressRepo;
    private readonly IStudentRepository _studentRepo;
    private readonly ILessonRepository _lessonRepo;
    private readonly ICertificateService _certificateService;
    private readonly IEmailService _emailService;
    private readonly IElastiCacheService _cache;
    private readonly ITenantContext _tenant;
    private readonly ILogger<ProgressController> _logger;

    // Constructor injection of 8 dependencies — and this grows as features are added
    public ProgressController(/* 8 services injected */) { /* ... */ }

    [HttpPost]
    public async Task<IActionResult> SubmitProgress(SubmitProgressRequest request)
    {
        // All business logic lives in the controller
        // Hard to test, hard to maintain, hard to extend
        _logger.LogInformation("Submitting progress for school {SchoolId}", _tenant.SchoolId);
        // ... 50 lines of mixed business and plumbing logic ...
    }
}
```

With MediatR, the controller becomes a thin dispatcher:

```csharp
// ✅ WITH MediatR: Controller is clean — one dependency, clear intent
[ApiController]
public class ProgressController : ControllerBase
{
    private readonly IMediator _mediator;  // Only one dependency!

    public ProgressController(IMediator mediator) => _mediator = mediator;

    [HttpPost]
    public async Task<IActionResult> SubmitProgress(SubmitProgressRequest request)
    {
        var result = await _mediator.Send(new SubmitLessonProgressCommand(
            Unit: request.Unit,
            LessonNumber: request.LessonNumber,
            ScorePercent: request.ScorePercent));

        return Ok(result);
    }

    [HttpGet("{studentId:int}")]
    public async Task<IActionResult> GetProgress(int studentId)
    {
        var result = await _mediator.Send(new GetStudentProgressQuery(studentId));
        return Ok(result);
    }
}
```

The controller no longer knows *how* progress is saved, or what happens after a quiz is submitted (emails, certificates, analytics). It simply sends a named, strongly-typed message and trusts the system to handle it.

---

## 4. MediatR Commands, Queries, and Notifications

MediatR distinguishes between three types of messages, each serving a different purpose:

### Commands — "Do Something, Tell Me the Result"

Commands represent **write operations** — they change state. They have exactly one handler.

```csharp
// ─────────────────────────────────────────────────────────────────
// Commands/SubmitLessonProgressCommand.cs
// "Record that a student completed a Grapeseed lesson"
// ─────────────────────────────────────────────────────────────────
public record SubmitLessonProgressCommand(
    string Unit,
    int LessonNumber,
    int ScorePercent
) : IRequest<SubmitProgressResponse>, ITenantRequest
{
    public string SchoolId { get; set; } = string.Empty; // Set by TenantValidationBehavior
}

public record SubmitProgressResponse(
    bool IsCompleted,
    int ScorePercent,
    string Message,
    string? CertificateUrl  // null if not earned yet
);

// The handler — focused on business logic only
public class SubmitLessonProgressCommandHandler
    : IRequestHandler<SubmitLessonProgressCommand, SubmitProgressResponse>
{
    private readonly GrapeseekWriteDbContext _db;
    private readonly IGrapeseekEventBus _eventBus;
    private readonly ILogger<SubmitLessonProgressCommandHandler> _logger;

    public SubmitLessonProgressCommandHandler(
        GrapeseekWriteDbContext db,
        IGrapeseekEventBus eventBus,
        ILogger<SubmitLessonProgressCommandHandler> logger)
    {
        _db = db;
        _eventBus = eventBus;
        _logger = logger;
    }

    public async Task<SubmitProgressResponse> Handle(
        SubmitLessonProgressCommand command,
        CancellationToken ct)
    {
        // Business rule: 70% score required to complete a Grapeseed lesson
        var isCompleted = command.ScorePercent >= 70;

        var progress = new LessonProgress
        {
            StudentId = GetCurrentStudentId(),   // From HttpContext via injected ICurrentUser
            Unit = command.Unit,
            LessonNumber = command.LessonNumber,
            ScorePercent = command.ScorePercent,
            IsCompleted = isCompleted,
            CompletedAt = isCompleted ? DateTime.UtcNow : null
            // SchoolId is auto-set by DbContext.SaveChangesAsync override
        };

        _db.LessonProgress.Add(progress);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Progress submitted: Student in school {SchoolId}, {Unit} Lesson {Lesson}, Score {Score}%, Completed: {IsCompleted}",
            command.SchoolId, command.Unit, command.LessonNumber, command.ScorePercent, isCompleted);

        // Publish event to SQS for background processing (email, certificate, analytics)
        await _eventBus.PublishAsync(new LessonCompletedMessage
        {
            SchoolId = command.SchoolId,
            StudentId = progress.StudentId,
            Unit = command.Unit,
            LessonNumber = command.LessonNumber,
            ScorePercent = command.ScorePercent,
            IsCompleted = isCompleted
        }, "lesson-completed", ct);

        return new SubmitProgressResponse(
            IsCompleted: isCompleted,
            ScorePercent: command.ScorePercent,
            Message: isCompleted
                ? $"🎉 You completed {command.Unit}, Lesson {command.LessonNumber}!"
                : "Keep practicing! You need 70% to complete this lesson.",
            CertificateUrl: null  // NotificationService will generate it asynchronously
        );
    }
    
    private int GetCurrentStudentId() => /* from ICurrentUser service */ 0;
}
```

### Queries — "Give Me Data, Don't Change Anything"

Queries represent **read operations**. They return data and must not change state.

```csharp
// ─────────────────────────────────────────────────────────────────
// Queries/GetStudentProgressQuery.cs
// "What has this student completed in Grapeseed?"
// ─────────────────────────────────────────────────────────────────
public record GetStudentProgressQuery(int StudentId)
    : IRequest<StudentProgressResponse>, ITenantRequest, ICachedQuery
{
    public string SchoolId { get; set; } = string.Empty;

    // Cache this response in ElastiCache for 5 minutes
    // (progress updates frequently during study sessions)
    public string CacheKey => $"student-progress:{SchoolId}:{StudentId}";
    public TimeSpan CacheDuration => TimeSpan.FromMinutes(5);
}

public record StudentProgressResponse(
    int StudentId,
    string StudentName,
    string CurrentUnit,
    int TotalLessonsCompleted,
    double OverallAverageScore,
    IReadOnlyList<UnitProgressSummary> UnitProgress
);

public record UnitProgressSummary(
    string Unit,
    int LessonsCompleted,
    int TotalLessons,
    double AverageScore,
    bool IsUnitComplete
);

// The query handler — uses Read Replica, no writes
public class GetStudentProgressQueryHandler
    : IRequestHandler<GetStudentProgressQuery, StudentProgressResponse>
{
    private readonly GrapeseekReadDbContext _readDb; // Read Replica
    private readonly ILogger<GetStudentProgressQueryHandler> _logger;

    public GetStudentProgressQueryHandler(
        GrapeseekReadDbContext readDb,
        ILogger<GetStudentProgressQueryHandler> logger)
    {
        _readDb = readDb;
        _logger = logger;
    }

    public async Task<StudentProgressResponse> Handle(
        GetStudentProgressQuery request,
        CancellationToken ct)
    {
        _logger.LogDebug("Fetching progress for student {StudentId}", request.StudentId);

        // Single efficient query — no N+1, returns only what we need
        // GlobalQueryFilter automatically adds WHERE SchoolId = @schoolId
        var student = await _readDb.Students
            .Where(s => s.Id == request.StudentId)
            .Select(s => new
            {
                s.Id,
                s.Name,
                s.CurrentUnit,
                Progress = s.Progress
                    .GroupBy(p => p.Unit)
                    .Select(g => new
                    {
                        Unit = g.Key,
                        LessonsCompleted = g.Count(p => p.IsCompleted),
                        AverageScore = g.Where(p => p.ScorePercent > 0).Average(p => (double?)p.ScorePercent) ?? 0
                    })
                    .ToList()
            })
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException($"Student {request.StudentId} not found.");

        var unitProgress = student.Progress
            .Select(p => new UnitProgressSummary(
                Unit: p.Unit,
                LessonsCompleted: p.LessonsCompleted,
                TotalLessons: GetTotalLessonsForUnit(p.Unit), // from config
                AverageScore: p.AverageScore,
                IsUnitComplete: p.LessonsCompleted >= GetTotalLessonsForUnit(p.Unit)
            ))
            .ToList();

        return new StudentProgressResponse(
            StudentId: student.Id,
            StudentName: student.Name,
            CurrentUnit: student.CurrentUnit,
            TotalLessonsCompleted: student.Progress.Sum(p => p.LessonsCompleted),
            OverallAverageScore: student.Progress.Any()
                ? student.Progress.Average(p => p.AverageScore) : 0,
            UnitProgress: unitProgress
        );
    }

    private int GetTotalLessonsForUnit(string unit) =>
        unit switch { "Unit 1" => 12, "Unit 2" => 12, "Unit 3" => 15, _ => 12 };
}
```

### Notifications — "Tell Everyone Who Cares"

Notifications are published to **zero or more handlers**. Perfect for in-process fan-out within a service (as opposed to cross-service events, which go through SQS).

```csharp
// ─────────────────────────────────────────────────────────────────
// Notifications/StudentReachedMilestoneNotification.cs
// Published when a student reaches a significant Grapeseed milestone
// Multiple handlers can react to this within the same service
// ─────────────────────────────────────────────────────────────────
public record StudentReachedMilestoneNotification(
    int StudentId,
    string SchoolId,
    string MilestoneType,    // "unit_complete", "first_lesson", "10_lessons"
    string Unit
) : INotification;

// Handler 1: Update the student's profile with the milestone badge
public class UpdateMilestoneBadgeHandler : INotificationHandler<StudentReachedMilestoneNotification>
{
    private readonly GrapeseekWriteDbContext _db;

    public async Task Handle(StudentReachedMilestoneNotification notification, CancellationToken ct)
    {
        // Add the achievement badge to the student's profile
        var badge = new StudentBadge
        {
            StudentId = notification.StudentId,
            BadgeType = notification.MilestoneType,
            Unit = notification.Unit,
            EarnedAt = DateTime.UtcNow
        };
        _db.StudentBadges.Add(badge);
        await _db.SaveChangesAsync(ct);
    }
}

// Handler 2: Invalidate the student's progress cache so they see the badge immediately
public class InvalidateProgressCacheHandler : INotificationHandler<StudentReachedMilestoneNotification>
{
    private readonly IDistributedCache _cache;
    private readonly ILogger<InvalidateProgressCacheHandler> _logger;

    public async Task Handle(StudentReachedMilestoneNotification notification, CancellationToken ct)
    {
        var cacheKey = $"student-progress:{notification.SchoolId}:{notification.StudentId}";
        await _cache.RemoveAsync(cacheKey, ct);
        _logger.LogInformation("Invalidated progress cache for student {StudentId}", notification.StudentId);
    }
}

// How to publish a notification in a command handler:
public class SomeCommandHandler : IRequestHandler<SomeCommand, SomeResponse>
{
    private readonly IMediator _mediator;

    public async Task<SomeResponse> Handle(SomeCommand command, CancellationToken ct)
    {
        // ... business logic ...

        // Publish to all registered INotificationHandlers
        await _mediator.Publish(new StudentReachedMilestoneNotification(
            StudentId: 123,
            SchoolId: command.SchoolId,
            MilestoneType: "unit_complete",
            Unit: "Unit 3"
        ), ct);

        return new SomeResponse();
    }
}
```

---

## 5. MediatR Pipeline Behaviors — The Full Stack

We've seen individual behaviors in earlier chapters. Here is the **complete MediatR pipeline** for a Grapeseed service, showing all behaviors and their order:

```
HTTP Request → Controller → _mediator.Send(query/command)
                                        │
                            ┌───────────▼──────────────┐
                            │    MediatR Pipeline        │
                            │                           │
                            │  1. LoggingBehavior       │ ← Log request start/end + duration
                            │         │                 │
                            │  2. TenantValidation      │ ← Validate school, stamp SchoolId
                            │         │                 │
                            │  3. FluentValidation      │ ← Validate request DTOs (rules)
                            │         │                 │
                            │  4. CachingBehavior       │ ← Check ElastiCache (for ICachedQuery)
                            │         │                 │
                            │  5. Handler               │ ← Actual business logic
                            │                           │
                            └───────────────────────────┘
```

```csharp
// ─────────────────────────────────────────────────────────────────
// Program.cs — Complete MediatR pipeline registration
// ─────────────────────────────────────────────────────────────────
builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssemblyContaining<Program>();

    // ORDER MATTERS — behaviors wrap each other like middleware layers
    // Outermost first: LoggingBehavior → TenantValidation → Validation → Caching → Handler
    cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
    cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(TenantValidationBehavior<,>));
    cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
    cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(CachingBehavior<,>));
});
```

```csharp
// ─────────────────────────────────────────────────────────────────
// Behaviors/LoggingBehavior.cs — Logs every MediatR request
// ─────────────────────────────────────────────────────────────────
public class LoggingBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly ILogger<LoggingBehavior<TRequest, TResponse>> _logger;

    public LoggingBehavior(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
        => _logger = logger;

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken ct)
    {
        var requestName = typeof(TRequest).Name;
        var stopwatch = Stopwatch.StartNew();

        _logger.LogInformation("→ Handling {RequestName}", requestName);

        try
        {
            var response = await next();
            stopwatch.Stop();
            _logger.LogInformation("✓ Handled {RequestName} in {ElapsedMs}ms",
                requestName, stopwatch.ElapsedMilliseconds);
            return response;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "✗ Error handling {RequestName} after {ElapsedMs}ms",
                requestName, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }
}

// ─────────────────────────────────────────────────────────────────
// Behaviors/ValidationBehavior.cs — FluentValidation integration
// ─────────────────────────────────────────────────────────────────
// Install: dotnet add package FluentValidation.AspNetCore
public class ValidationBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly IEnumerable<IValidator<TRequest>> _validators;

    public ValidationBehavior(IEnumerable<IValidator<TRequest>> validators)
        => _validators = validators;

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken ct)
    {
        if (_validators.Any())
        {
            var context = new ValidationContext<TRequest>(request);
            var results = await Task.WhenAll(
                _validators.Select(v => v.ValidateAsync(context, ct)));

            var failures = results
                .SelectMany(r => r.Errors)
                .Where(f => f is not null)
                .ToList();

            if (failures.Any())
                throw new ValidationException(failures);
        }

        return await next();
    }
}

// Example validator for a Grapeseed command
public class SubmitLessonProgressCommandValidator 
    : AbstractValidator<SubmitLessonProgressCommand>
{
    public SubmitLessonProgressCommandValidator()
    {
        RuleFor(x => x.Unit)
            .NotEmpty()
            .Matches(@"^Unit \d{1,2}$")
            .WithMessage("Unit must be in format 'Unit 1', 'Unit 2', etc.");

        RuleFor(x => x.LessonNumber)
            .InclusiveBetween(1, 20)
            .WithMessage("Lesson number must be between 1 and 20.");

        RuleFor(x => x.ScorePercent)
            .InclusiveBetween(0, 100)
            .WithMessage("Score must be between 0 and 100.");
    }
}
```

---

## 6. Domain-Driven Design — Finding Service Boundaries

The hardest part of microservices is knowing where to cut. Cut wrong and you get a **distributed monolith** — all the costs of microservices with none of the benefits. DDD's concept of **Bounded Contexts** gives us a principled approach.

### The "Student" Problem in Grapeseed

The word "student" means something different in each context:

| Service Context | "Student" means... |
|----------------|-------------------|
| **IdentityService** | A user account: email, password hash, role ("student") |
| **LessonService** | A learner: current unit, level, lesson assignments |
| **ProgressService** | A record-keeper: completed lessons, quiz scores, certificates |
| **VideoService** | A viewer: watch history, resume position, video preferences |
| **NotificationService** | A recipient: email, push notification preferences, last notified |
| **AnalyticsService** | A data point: aggregate scores, usage patterns, time-on-platform |

If you build one giant `Student` class with 60 properties covering all of these, it becomes incoherent and unmaintainable. Instead, each service has its own lean model of a student, referencing others only by `StudentId`.

### Finding Grapeseed's Bounded Contexts

Look for these signals:
- **Different rate of change** — Identity credentials change for security reasons; lesson content changes for curriculum reasons; billing changes for business reasons
- **Different teams** — If a different team owns it, it's likely a different bounded context
- **Different language** — When two teams call the same thing by different names, or the same word means different things

---

## 7. Designing Grapeseed's Services

```
┌─────────────────────────────────────────────────────────────────────┐
│                   Grapeseed Service Map                             │
│                                                                      │
│  ┌────────────────────────────────────────────────────────────────┐ │
│  │                  AWS API Gateway                                │ │
│  │  Routing · JWT Auth (Cognito/Custom) · Rate Limiting · WAF     │ │
│  └──────┬──────────────┬──────────────────┬──────────────┬────────┘ │
│         │              │                  │              │           │
│  ┌──────▼───┐  ┌───────▼────┐  ┌─────────▼──┐  ┌───────▼───────┐  │
│  │Identity  │  │  Lesson    │  │  Progress  │  │   Notification│  │
│  │Service   │  │  Service   │  │  Service   │  │   Service     │  │
│  │          │  │            │  │            │  │               │  │
│  │Auth/Login│  │Lessons,    │  │Student     │  │Email via SES  │  │
│  │JWT tokens│  │Quizzes,    │  │scores,     │  │Push via SNS   │  │
│  │Password  │  │Assignments │  │Certificates│  │In-app alerts  │  │
│  │          │  │            │  │            │  │               │  │
│  │RDS PG    │  │RDS PG      │  │RDS PG      │  │No DB (SQS)    │  │
│  └──────────┘  └────────────┘  └────────────┘  └───────────────┘  │
│                                                                      │
│  ┌───────────────┐  ┌─────────────────────────────────────────────┐ │
│  │  Video        │  │  Analytics Service                          │ │
│  │  Service      │  │                                             │ │
│  │               │  │  School dashboards, platform reports        │ │
│  │  Upload,      │  │  Usage statistics, completion rates         │ │
│  │  Transcode,   │  │                                             │ │
│  │  Stream       │  │  RDS SQL Server (T-SQL reporting queries)   │ │
│  │               │  │  + Amazon QuickSight for dashboards         │ │
│  │  S3 +         │  └─────────────────────────────────────────────┘ │
│  │  CloudFront   │                                                   │
│  │  + MediaConvert│  ┌─────────────────────────────────────────────┐ │
│  └───────────────┘  │  SchoolService (Tenant Management)           │ │
│                      │  School settings, license management         │ │
│                      │  Subdomain → SchoolId mapping               │ │
│                      │  RDS PostgreSQL                             │ │
│                      └─────────────────────────────────────────────┘ │
│                                                                      │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │   Amazon SQS / SNS  (Cross-Service Async Event Messaging)    │   │
│  └──────────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────────┘
```

**Tech stack per service:**

| Service | Database | Key AWS Services |
|---------|----------|-----------------|
| IdentityService | RDS PostgreSQL | Cognito (or custom), Secrets Manager |
| LessonService | RDS PostgreSQL | ElastiCache, SQS |
| ProgressService | RDS PostgreSQL | ElastiCache, SQS, S3 (certificates) |
| VideoService | S3 | CloudFront, MediaConvert, ElastiCache |
| NotificationService | None (stateless) | SQS, SES, SNS |
| AnalyticsService | RDS **SQL Server** | QuickSight, S3 (data exports) |
| SchoolService | RDS PostgreSQL | ElastiCache, Secrets Manager |

Note that **AnalyticsService uses SQL Server** — this is intentional. Complex OLAP queries, window functions, and reporting CTEs are where the analytics team has expertise in T-SQL. EF Core's SQL Server provider works seamlessly alongside the PostgreSQL provider in other services.

---

## 8. Service Communication — Sync vs. Async

### Synchronous (HTTP between services)

Use HTTP when the calling service needs the response immediately.

```csharp
// LessonService needs video metadata to build the lesson page response.
// It cannot return the lesson without this data — synchronous is correct.
public class GetLessonWithVideoQueryHandler : IRequestHandler<GetLessonWithVideoQuery, LessonDetailResponse>
{
    private readonly GrapeseekReadDbContext _readDb;
    private readonly IVideoServiceClient _videoClient; // HTTP client with Polly

    public async Task<LessonDetailResponse> Handle(GetLessonWithVideoQuery request, CancellationToken ct)
    {
        // 1. Get lesson from this service's database
        var lesson = await _readDb.Lessons
            .FirstOrDefaultAsync(l => l.Unit == request.Unit && l.LessonNumber == request.LessonNumber, ct)
            ?? throw new NotFoundException("Lesson not found");

        // 2. Get video metadata from VideoService (synchronous HTTP call)
        var videoMetadata = await _videoClient.GetVideoMetadataAsync(lesson.VideoId, ct);
        
        // If VideoService is down (circuit breaker open), videoMetadata is null/fallback
        // The lesson page still renders — just without video details
        
        return new LessonDetailResponse
        {
            Lesson = lesson,
            Video = videoMetadata
        };
    }
}
```

### Asynchronous (SQS between services)

Use SQS when the action has side effects that don't need to complete before the user gets their response.

```
Student submits quiz answer → ProgressService saves it → returns "Score: 85%" to student
                                                ↓ (async, fire-and-forget)
                                         SQS: "lesson-completed" queue
                                         ├─► NotificationService: send email
                                         ├─► AnalyticsService: update stats
                                         └─► ProgressService: update unit completion
```

The student receives their score in ~50ms. The email, analytics update, and unit completion check happen over the next few seconds, invisibly in the background.

---

## 9. AWS API Gateway — The Front Door

AWS API Gateway acts as Grapeseed's single entry point for all client requests. It provides:

- **JWT validation** — validates Bearer tokens before forwarding to ECS services
- **Routing** — forwards requests to the correct ECS service based on the URL path
- **Rate limiting** — per-API-key or per-IP rate limiting at the edge (before ECS is involved)
- **AWS WAF integration** — blocks malicious requests before they reach your services
- **SSL termination** — HTTPS from client to API Gateway; internal traffic can be plain HTTP within the VPC

```yaml
# API Gateway routes (simplified — defined in AWS Console, CDK, or Terraform)

/api/auth/**        → IdentityService ALB (no auth required — login endpoint)
/api/lessons/**     → LessonService ALB     (JWT required)
/api/progress/**    → ProgressService ALB   (JWT required)
/api/videos/**      → VideoService ALB      (JWT required)
/api/analytics/**   → AnalyticsService ALB  (JWT + Admin role required)
/api/schools/**     → SchoolService ALB     (JWT + Admin role required)
```

```csharp
// ─────────────────────────────────────────────────────────────────
// Each ECS service trusts the API Gateway's auth validation.
// Services still validate the JWT claims for fine-grained authorization,
// but they don't need to call IdentityService on every request —
// the JWT contains all necessary claims.
// ─────────────────────────────────────────────────────────────────
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // The signing key is loaded from AWS Secrets Manager
        options.Authority = builder.Configuration["Auth:Issuer"];
        options.Audience = "grapeseed-api";
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true
        };
    });
```

---

## 10. Amazon SQS/SNS — Async Event Bus Between Services

Grapeseed uses SQS and SNS for cross-service async communication.

- **SQS (Simple Queue Service):** Point-to-point queues. One producer, one consumer group. Messages are deleted after successful processing.
- **SNS (Simple Notification Service):** Fan-out (publish/subscribe). One producer, many consumers. Each consumer gets its own SQS queue fed by SNS.

```
SQS Only (point-to-point):
  ProgressService → SQS queue → NotificationService
  (Only NotificationService consumes this queue)

SNS + SQS Fan-out (one event, many consumers):
  ProgressService → SNS Topic: "student-unit-completed"
                          │
              ┌───────────┼───────────┐
              │           │           │
          SQS Queue   SQS Queue   SQS Queue
              │           │           │
         Analytics   Certificate  Notification
          Service     Service      Service
  (All three react to the same event independently)
```

```csharp
// ─────────────────────────────────────────────────────────────────
// Publishing to an SNS topic (fan-out to multiple consumers)
// ─────────────────────────────────────────────────────────────────
public class SnsEventBus : IGrapeseekEventBus
{
    private readonly IAmazonSimpleNotificationService _snsClient;
    private readonly IConfiguration _configuration;

    public async Task PublishAsync<T>(T message, string topicName, CancellationToken ct = default)
        where T : class
    {
        var topicArn = _configuration[$"AWS:SNS:Topics:{topicName}"];
        var messageJson = JsonSerializer.Serialize(message);

        await _snsClient.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = messageJson,
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["MessageType"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = typeof(T).Name
                }
            }
        }, ct);
    }
}

// Usage in a command handler:
await _eventBus.PublishAsync(
    new StudentCompletedUnitEvent
    {
        SchoolId = command.SchoolId,
        StudentId = studentId,
        Unit = "Unit 3",
        CompletedAt = DateTime.UtcNow
    },
    topicName: "student-unit-completed",  // SNS Topic → fan-out to 3 SQS queues
    ct);
```

---

## 11. The Saga Pattern — Distributed Transactions

**The problem:** When enrolling a new student in Grapeseed, multiple services must all succeed:
1. IdentityService: create user account
2. SchoolService: assign a school license
3. LessonService: initialize lesson assignments for the school's curriculum
4. NotificationService: send welcome email

In a monolith with one database, this is a single database transaction — if step 3 fails, steps 1 and 2 are rolled back automatically.

In microservices with separate databases, there is no single transaction. If step 3 fails after steps 1 and 2 completed, the student has an account and a license but no lesson assignments. The system is in an inconsistent state.

**The Saga pattern** solves this with a sequence of local transactions, each publishing an event. If any step fails, **compensating transactions** undo what was already done.

```
Enrollment Saga — Success Path:
─────────────────────────────────
  1. SchoolService: creates enrollment request
     → publishes: "CreateUserAccountCommand" to IdentityService
  
  2. IdentityService: creates account (StudentId: 901)
     → publishes: "UserAccountCreated" event
  
  3. SchoolService: hears UserAccountCreated → assigns license
     → publishes: "LicenseAssigned" event
  
  4. LessonService: hears LicenseAssigned → creates lesson assignments
     → publishes: "LessonAssignmentsCreated" event
  
  5. NotificationService: hears LessonAssignmentsCreated → sends welcome email
     → Saga complete ✅

Enrollment Saga — Failure Path (step 4 fails — no licenses left):
──────────────────────────────────────────────────────────────────
  1-3. Same as above.
  
  4. SchoolService tries to assign license → fails (no licenses remaining!)
     → publishes: "EnrollmentFailed" event
  
  5. IdentityService hears EnrollmentFailed → COMPENSATES by deleting the account
  
  6. SchoolService updates enrollment status to "Failed — No Licenses"
  
  Final state: clean. No orphaned account. Error reported to admin. ✅
```

The implementation of a production Saga can use the **MediatR notification system** for in-service coordination and **SQS/SNS** for cross-service coordination, or dedicated Saga frameworks like NServiceBus or MassTransit (which supports AWS SQS as its transport).

---

## 12. Resilience with Polly Between Services

Every HTTP call from one Grapeseed service to another must be resilient. We covered Polly in Chapter 1. Here's the full setup for all service clients:

```csharp
// ─────────────────────────────────────────────────────────────────
// Program.cs in LessonService — register all downstream HTTP clients
// ─────────────────────────────────────────────────────────────────
var combinedPolicy = Policy.WrapAsync(
    ResiliencePolicies.GetCircuitBreakerPolicy(),
    ResiliencePolicies.GetRetryPolicy());

builder.Services
    .AddHttpClient<IVideoServiceClient, VideoServiceClient>(c =>
        c.BaseAddress = new Uri(config["ServiceUrls:VideoService"]!))
    .AddPolicyHandler(combinedPolicy);

builder.Services
    .AddHttpClient<IProgressServiceClient, ProgressServiceClient>(c =>
        c.BaseAddress = new Uri(config["ServiceUrls:ProgressService"]!))
    .AddPolicyHandler(combinedPolicy);

builder.Services
    .AddHttpClient<ISchoolServiceClient, SchoolServiceClient>(c =>
        c.BaseAddress = new Uri(config["ServiceUrls:SchoolService"]!))
    .AddPolicyHandler(combinedPolicy);
```

The Polly circuit breaker patterns from Chapter 1 apply here — if VideoService is repeatedly failing, the circuit opens, LessonService returns a fallback response, and students still get their lesson page (just without video metadata temporarily).

---

## 13. Distributed Tracing with AWS X-Ray

With 6+ services handling a single student request, debugging "why is the lesson page slow?" requires seeing the **full request journey** across all services.

**AWS X-Ray** provides distributed tracing, integrated with ECS, API Gateway, and the .NET SDK.

```
X-Ray Trace: "GET /api/lessons/Unit3/1" — Total: 650ms
────────────────────────────────────────────────────────────
[API Gateway]          0ms    ─────────────────────── 650ms
  [LessonService]      12ms   ──────────────────────── 630ms
    [ElastiCache]      14ms   ─ 2ms (MISS — first hit of day)
    [RDS Read Replica] 17ms   ──── 8ms (lesson content query OK)
    [VideoService]     26ms   ──────────────────────── 610ms  ← SLOW
      [RDS Read Replica] 28ms ─────────────────────── 600ms  ← Slow query!
        Missing Index!  ← X-Ray shows this query took 600ms
```

```csharp
// Install: dotnet add package AWSSDK.XRay
//          dotnet add package Amazon.XRay.Recorder.Handlers.AspNetCore
//          dotnet add package Amazon.XRay.Recorder.Handlers.SqlServer

// ─────────────────────────────────────────────────────────────────
// Program.cs — X-Ray instrumentation
// ─────────────────────────────────────────────────────────────────
// Register the X-Ray recorder
AWSXRayRecorder.InitializeInstance(builder.Configuration);

var app = builder.Build();

// X-Ray middleware — creates a segment for each HTTP request
app.UseXRay("GrapeseekLessonService");

// Note: For ECS Fargate, the X-Ray daemon runs as a sidecar container.
// Add to your ECS task definition:
// {
//   "name": "xray-daemon",
//   "image": "amazon/aws-xray-daemon",
//   "essential": false
// }
```

```csharp
// ─────────────────────────────────────────────────────────────────
// Adding custom X-Ray annotations in MediatR handlers
// ─────────────────────────────────────────────────────────────────
public class GetLessonWithVideoQueryHandler : IRequestHandler<GetLessonWithVideoQuery, LessonDetailResponse>
{
    public async Task<LessonDetailResponse> Handle(GetLessonWithVideoQuery request, CancellationToken ct)
    {
        // Add custom metadata to the X-Ray trace
        AWSXRayRecorder.Instance.AddAnnotation("SchoolId", request.SchoolId);
        AWSXRayRecorder.Instance.AddAnnotation("Unit", request.Unit);
        AWSXRayRecorder.Instance.AddAnnotation("LessonNumber", request.LessonNumber.ToString());

        // Create a sub-segment for the video service call
        return await AWSXRayRecorder.Instance.TraceMethodAsync(
            "GetLessonWithVideo",
            async () =>
            {
                var lesson = await GetLessonAsync(request);
                var video = await GetVideoMetadataAsync(lesson.VideoId, ct);
                return BuildResponse(lesson, video);
            });
    }
}
```

X-Ray traces are visible in the **AWS X-Ray Console** and can be correlated with **CloudWatch Logs** via the Trace ID header that propagates through all HTTP calls.

---

## 14. The Grapeseed Scenario — Student Enrollment

Let's trace a complete student enrollment through Grapeseed's microservices:

```
A school administrator uploads 300 new student emails via the School Admin Portal.

1. POST /api/schools/bulk-enroll → AWS API Gateway
   - JWT validated (admin role confirmed)
   - Request forwarded to SchoolService

2. SchoolService MediatR pipeline:
   LoggingBehavior → TenantValidationBehavior → ValidationBehavior → Handler
   
   BulkEnrollStudentsCommandHandler:
   - Validates CSV format
   - For each student: publishes "EnrollStudentCommand" to SQS queue
   - Returns: { "message": "300 enrollments queued", "trackingId": "bulk-001" }
   
   Admin sees result in 200ms. They don't wait for 300 enrollments.

3. EnrollmentProcessor (background ECS task reads SQS):
   For each student, the Saga executes:
   
   Step 1: IdentityService — POST /api/identity/create-student-account
           → { studentId: 9001, email: "student@school.th" }
           → Publishes: "UserAccountCreated" to SNS topic
   
   Step 2: SchoolService — hears UserAccountCreated
           → Assigns 1 of school's 300 remaining licenses
           → Publishes: "LicenseAssigned" to SNS topic
   
   Step 3: LessonService — hears LicenseAssigned
           → Creates lesson assignments (Units 1-6, all lessons) for this student
           → EF Core bulk insert with tenant auto-fill (SchoolId auto-set)
           → Publishes: "LessonAssignmentsCreated" to SNS topic
   
   Step 4: NotificationService — hears LessonAssignmentsCreated
           → Sends welcome email via Amazon SES:
             "Welcome to Grapeseed! Your login is ready at school.grapeseed.com"
   
   FAILURE CASE: If the school has only 250 licenses and tries to enroll 300:
   - First 250 students: succeed (LicenseAssigned)
   - Students 251-300: "LicenseAssignmentFailed" event published
   - Saga compensating action: IdentityService deletes accounts 251-300
   - Admin dashboard shows: "250 enrolled ✅, 50 failed ❌ (insufficient licenses)"

4. After 300 enrollments (typically 2-3 minutes):
   - Admin dashboard shows real-time status updates (via WebSocket or polling)
   - All 250 students can log in immediately
   - Their lesson dashboards show Unit 1, Lesson 1 ready to start
```

---

## 15. When NOT to Use Microservices

This section may be the most important in the chapter.

### Signs You're Not Ready

**"We have 3 developers."**
Each microservice needs its own ECS service, RDS instance, CI/CD pipeline, CloudWatch alarms, and on-call rotation. Three developers will spend 80% of their time on infrastructure operations.

**"Our codebase is messy."**
Messy code becomes messy microservices. Fix the architecture inside the monolith first. MediatR CQRS with clean command/query handlers is a great step — it's the same pattern you'd use in microservices, just within one process.

**"We want to use different AWS services."**
That's fine — you can use different RDS configurations, different S3 buckets, and different ECS task sizes within a monolith. Microservices are about team ownership and deployment independence, not just technology choices.

### The Strangler Fig Pattern for Grapeseed

If starting from a monolith, extract one service at a time:

```
Phase 1: Monolith handles everything
  Client → ALB → GrapeseekMonolith ECS → Single RDS PostgreSQL

Phase 2: Extract NotificationService (fewest dependencies)
  Client → API Gateway → NotificationService ECS (new)
                      → GrapeseekMonolith (shrinking)

Phase 3: Extract VideoService (different scaling profile)
  Client → API Gateway → VideoService ECS (new)
                      → NotificationService ECS
                      → GrapeseekMonolith (shrinking)

...continue until the monolith is fully decomposed...
```

### Recommended Extraction Order for Grapeseed

| Phase | Service | Reason to Extract |
|-------|---------|------------------|
| 1 | NotificationService | Stateless, fewest dependencies, easy to test independently |
| 2 | VideoService | Very different scaling profile (CPU-intensive transcoding) |
| 3 | AnalyticsService | SQL Server, different tech, different team |
| 4 | ProgressService | High write volume on exam day — independent scaling valuable |
| 5 | LessonService | High read volume — independent caching and read replica strategy |
| 6 | SchoolService | Tenant management — foundational, extract carefully |
| Last | IdentityService | Most critical, most complex, extract when team is experienced |

---

## 16. Summary and Key Takeaways

### Core Concepts

| Concept | One-Line Summary |
|---------|-----------------|
| MediatR | In-process mediator: decouples controllers from business logic via typed messages |
| Command | A MediatR request that changes state. One handler. Returns a result. |
| Query | A MediatR request that reads data. One handler. Must not change state. |
| Notification | A MediatR message broadcast to zero or more handlers (in-process fan-out) |
| Pipeline Behavior | MediatR middleware: logging, tenant validation, FluentValidation, caching |
| Bounded Context | The domain boundary that defines a natural microservice |
| API Gateway | AWS-managed entry point: routing, JWT validation, rate limiting, WAF |
| SQS | Point-to-point queue for async events between services |
| SNS + SQS | Fan-out: one SNS topic → multiple SQS queues → multiple consumers |
| Saga Pattern | Multi-step distributed transaction with compensating rollback on failure |
| X-Ray | AWS distributed tracing — see the full request journey across all services |
| Strangler Fig | Safe pattern: extract one service at a time, don't rewrite everything at once |

### The MediatR Architecture Blueprint

Every Grapeseed service follows this structure:

```
Controller → IMediator.Send(Command/Query)
                  │
          MediatR Pipeline:
            LoggingBehavior
            TenantValidationBehavior
            ValidationBehavior (FluentValidation)
            CachingBehavior (for ICachedQuery)
                  │
            CommandHandler → GrapeseekWriteDbContext (RDS Primary)
            QueryHandler   → GrapeseekReadDbContext  (RDS Read Replica)
                  │
            Publish events → SQS/SNS (cross-service)
            Publish notifications → IMediator.Publish() (in-process)
```

This is the pattern. Once you understand it, every Grapeseed service is instantly familiar — regardless of which team wrote it.

---

*Congratulations on completing the System Architecture Mastery book!*

*You have learned:*
- *How distributed systems work on AWS — and why they're challenging*
- *How Grapeseed serves hundreds of schools securely with multi-tenancy and MediatR pipeline behaviors*
- *How CloudFront, ElastiCache, ECS Auto Scaling, and SQS handle massive traffic spikes*
- *How MediatR CQRS organizes service internals, and how microservices organize the overall system*

*→ Return to [Book Index](./book_INDEX.md)*

---

*Chapter 4 Complete · 16 sections · Microservices & MediatR on AWS*
*System Architecture Mastery — Complete*
