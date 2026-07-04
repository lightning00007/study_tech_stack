# Chapter 2: Multi-Tenant Architecture

> **Advanced Architecture Pattern · Data Isolation · Enterprise SaaS on AWS**
> *"Multi-tenancy is the art of serving many schools from one system, while making each school feel like they have the platform all to themselves."*

---

## Table of Contents

1. [Introduction — One Platform, Many Schools](#1-introduction)
2. [What Is Multi-Tenancy?](#2-what-is-multi-tenancy)
3. [The Three Tenancy Models](#3-tenancy-models)
4. [Grapeseed's Choice — Hybrid Approach](#4-grapeseeds-choice)
5. [Tenant Isolation — Beyond Just Data](#5-tenant-isolation)
6. [Resolving the Tenant — Who Is Calling?](#6-resolving-the-tenant)
7. [Propagating Tenant Context Through the Stack](#7-propagating-tenant-context)
8. [EF Core Global Query Filters — Automatic Data Isolation](#8-ef-core-global-query-filters)
9. [MediatR Pipeline Behavior for Tenant Context](#9-mediatr-tenant-behavior)
10. [Row-Level Security in PostgreSQL and SQL Server](#10-row-level-security)
11. [Tenant-Aware Feature Flags and Configuration](#11-tenant-aware-features)
12. [Storing Tenant Data on AWS](#12-aws-tenant-storage)
13. [The Grapeseed Scenario](#13-grapeseed-scenario)
14. [Decision Guide](#14-decision-guide)
15. [Summary and Key Takeaways](#15-summary)

---

## 1. Introduction — One Platform, Many Schools

Imagine you run a franchise restaurant company. You have 300 restaurants in 20 countries. Each restaurant has its own staff, its own local menu adaptations, its own loyalty card members, and its own sales figures.

You could run separate software for each restaurant. That's 300 software deployments to maintain. When you fix a bug, you fix it 300 times. When you release a new feature (online ordering), you deploy it 300 times. Clearly, that doesn't scale.

Instead, your company runs one central system. Each restaurant logs in and sees only their own staff, their own menu, their own customers. They don't know — and don't care — that 299 other restaurants share the same software. From their perspective, they have their own private system.

**This is multi-tenancy.** In Grapeseed's world:
- Each school or school district is a **tenant**
- Grapeseed is the central software
- Every tenant gets a completely isolated experience on the same shared AWS infrastructure

Each Grapeseed school tenant needs:
- Their own students and teachers, completely isolated from other schools
- Their own lesson assignments and curriculum pacing
- Their own configuration (academic year settings, timezone, language settings)
- Their own branding (school logo, school name in emails)
- **Absolute guarantee that their data cannot leak to other schools**

---

## 2. What Is Multi-Tenancy?

**Multi-tenancy** is a software architecture where a single instance of an application serves multiple tenants (customers/organizations), with each tenant's data and configuration isolated from all others.

### The Core Promise

No matter what happens, these three guarantees must hold:

```
┌─────────────────────────────────────────────────────────────────┐
│              The Multi-Tenancy Guarantees                        │
│                                                                   │
│  1. DATA ISOLATION                                               │
│     School A can never read or write School B's data.           │
│     Not through bugs. Not through API manipulation.             │
│     Not through misconfigured IAM policies.                     │
│                                                                   │
│  2. CONFIGURATION ISOLATION                                      │
│     Changing School A's settings never affects School B.         │
│     Each school configures their own experience.                │
│                                                                   │
│  3. PERFORMANCE ISOLATION                                        │
│     School A running a heavy report export should not slow      │
│     down the lesson experience for students in School B.        │
└─────────────────────────────────────────────────────────────────┘
```

### The Business Case

Why go multi-tenant instead of deploying separate Grapeseed instances for each school?

| Concern | Separate Deployments | Multi-Tenant |
|---------|---------------------|--------------|
| AWS infrastructure cost | Very high (N ECS clusters, N RDS instances) | Low (shared) |
| Deployment | N deployments per release | 1 deployment |
| Bug fixes | Fix and deploy N times | Fix and deploy once |
| Scaling | Scale each school separately | Scale the whole platform |
| Onboard new school | Set up new AWS stack (hours/days) | Add a row to the DB (seconds) |
| Security updates | Apply to N systems | Apply once |

For Grapeseed serving 300 schools, multi-tenancy can reduce AWS costs by 80-90% and reduce operational overhead by even more.

---

## 3. The Three Tenancy Models

There is no single "correct" way to implement multi-tenancy. There are three fundamental approaches, each with different tradeoffs.

### Model 1: Separate Database per Tenant (Siloed)

Each tenant gets their own completely independent database.

```
Grapeseed Application (shared ECS service)
         │
         ├──► RDS PostgreSQL: school_bangkok_db
         ├──► RDS PostgreSQL: school_toronto_db
         ├──► RDS PostgreSQL: school_mumbai_db
         └──► RDS SQL Server: school_district_nyc_db  (enterprise requirement)
```

**✅ Pros:**
- Maximum data isolation — databases are physically separate
- Can comply with data residency laws (Bangkok school data stays in AWS ap-southeast-1)
- Backups are per-tenant — easy point-in-time restore for one school
- A slow query in one school's DB doesn't affect others

**❌ Cons:**
- Expensive — each RDS instance costs money even when idle
- Schema migrations must run against every database separately
- Connection management: your app needs to pick the right connection string per request
- Not practical beyond ~100 schools (becomes an operational nightmare)

**Best for:** Grapeseed's largest enterprise school districts that have data sovereignty requirements or pay a premium license.

---

### Model 2: Shared Database, Separate Schemas

One database, but each tenant gets their own schema (a namespace within the database).

```
One Database: grapeseed_db (RDS PostgreSQL)
  │
  ├── Schema: school_bangkok
  │     ├── students
  │     ├── lesson_progress
  │     └── lesson_assignments
  │
  ├── Schema: school_toronto
  │     ├── students
  │     ├── lesson_progress
  │     └── lesson_assignments
  │
  └── Schema: school_mumbai
        ├── students
        ├── lesson_progress
        └── lesson_assignments
```

**✅ Pros:**
- Good isolation — schemas are namespaces
- One RDS instance, lower cost than Model 1
- Easy to dump/restore one tenant's schema

**❌ Cons:**
- Schema migrations still run N times (once per schema)
- PostgreSQL supports this well; SQL Server supports it via schemas too, but tooling is less mature
- Not practical beyond ~500-1,000 schools

**Best for:** Mid-range B2B SaaS with dozens to hundreds of tenants.

---

### Model 3: Shared Database, Shared Tables (Discriminator Column)

One database, one set of tables for all tenants. Every row has a `SchoolId` column (the tenant discriminator).

```
One Database: grapeseed_db (RDS PostgreSQL)
One Schema: public

Students Table:
┌──────┬──────────────┬──────────────────┬────────────────────────┐
│ Id   │ SchoolId     │ Name             │ Email                  │
├──────┼──────────────┼──────────────────┼────────────────────────┤
│ 1    │ school-bkk   │ Siriporn T.      │ siriporn@abc-school... │
│ 2    │ school-tor   │ Emma Watson      │ emma@toronto-school... │
│ 3    │ school-bkk   │ Somchai P.       │ somchai@abc-school...  │
│ 4    │ school-mum   │ Priya Sharma     │ priya@mumbai-school... │
└──────┴──────────────┴──────────────────┴────────────────────────┘

Every query MUST include WHERE SchoolId = 'current_school'
```

**✅ Pros:**
- Lowest AWS cost — one RDS instance, one schema for potentially thousands of schools
- Schema migrations run once for all tenants
- Simplest operational model — one database to monitor and back up

**❌ Cons:**
- **Catastrophic if a developer forgets the SchoolId filter** — one bug could expose all schools' data
- "Noisy neighbor" — one school's heavy report can slow down query performance for others
- Cross-tenant admin queries require bypassing the filter carefully

**Best for:** The core Grapeseed platform serving the majority of standard schools.

---

## 4. Grapeseed's Choice — Hybrid Approach

Grapeseed uses a **hybrid of Model 1 and Model 3**:

```
Standard schools (Model 3 — Shared Tables):
  ├── 95% of schools
  ├── Cost-efficient
  └── Uses PostgreSQL on a shared RDS cluster

Enterprise/Government districts (Model 1 — Separate DB):
  ├── 5% of schools — premium accounts
  ├── Own RDS PostgreSQL or SQL Server instance
  ├── Own AWS region (for data residency compliance)
  └── Connected by a separate connection string resolved at runtime
```

The application code handles both through **dynamic connection string resolution**: the tenant resolution middleware determines which database connection to use for each request. For most schools, it uses the shared cluster. For premium schools, it connects to their dedicated RDS instance.

```csharp
// ─────────────────────────────────────────────────────────────────
// TenantConnectionResolver.cs — Dynamic connection string resolution
// ─────────────────────────────────────────────────────────────────
public class TenantConnectionResolver : ITenantConnectionResolver
{
    private readonly IDistributedCache _cache;
    private readonly AmazonSecretsManagerClient _secretsManager;

    public async Task<string> ResolveConnectionStringAsync(string schoolId)
    {
        // Check cache first — we don't want to hit Secrets Manager on every request
        var cacheKey = $"tenant_conn:{schoolId}";
        var cached = await _cache.GetStringAsync(cacheKey);
        if (cached is not null) return cached;

        // Check if this is a dedicated-database school (premium tier)
        // If yes, load their own connection string from Secrets Manager
        var secretName = $"grapeseed/schools/{schoolId}/db-connection";
        try
        {
            var response = await _secretsManager.GetSecretValueAsync(
                new GetSecretValueRequest { SecretId = secretName });

            var connectionString = response.SecretString;

            // Cache for 5 minutes — connection strings don't change often
            await _cache.SetStringAsync(cacheKey, connectionString,
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
                });

            return connectionString;
        }
        catch (ResourceNotFoundException)
        {
            // No school-specific secret → use the shared cluster connection string
            return _configuration["ConnectionStrings:SharedGrapeseekDb"];
        }
    }
}
```

---

## 5. Tenant Isolation — Beyond Just Data

True tenant isolation in Grapeseed covers multiple dimensions:

### Data Isolation
Students, progress records, lesson assignments, teacher accounts — all scoped by `SchoolId`.

### Configuration Isolation
Each school configures their own experience:
- Grapeseed unit pacing (how quickly students should progress through units)
- Academic year start/end dates
- Timezone (Asia/Bangkok vs. America/Toronto)
- Which Grapeseed units are licensed (some schools license Units 1-6, others Units 1-12)
- Notification preferences (email reports weekly vs. daily)

### Feature Isolation
Different Grapeseed license tiers unlock different features:
- **Basic:** Core lessons, quizzes, teacher dashboard
- **Standard:** + Progress reports, parent access, SIS integration
- **Premium:** + Live coaching sessions, AI pronunciation analysis, advanced analytics

### Branding Isolation
Each school sees:
- Their school name and logo on all pages and emails
- Their primary accent color on the platform
- Their administrative contact in the email footer
- Optional: their own subdomain (`bangkok.grapeseed.com`)

### Performance Isolation
One school running a massive CSV export should not slow down students in another school. Achieved via:
- Per-school rate limiting
- Heavy background jobs go to a separate SQS queue with lower priority
- Large reports run on the Analytics Service (separate from the lesson-serving path)

---

## 6. Resolving the Tenant — Who Is Calling?

Before enforcing any isolation, the application must answer: **which school is this request for?**

### Strategy A: JWT Claim-Based Resolution (Primary)

After a user logs in, the JWT access token contains a `school_id` claim:

```json
{
  "sub": "user-789",
  "email": "siriporn@abc-school.th",
  "school_id": "school-bkk-001",
  "role": "student",
  "exp": 1751234567
}
```

Every API call carries this JWT. The middleware reads `school_id` from the validated token. Secure — users cannot fake claims in a properly signed JWT.

### Strategy B: Subdomain-Based Resolution (For Login Page)

Each school gets a subdomain: `bangkok.grapeseed.com`, `toronto.grapeseed.com`. The middleware extracts the subdomain from the `Host` header before the user is logged in.

```csharp
// ─────────────────────────────────────────────────────────────────
// TenantResolutionMiddleware.cs — Resolves the current school/tenant
// ─────────────────────────────────────────────────────────────────
public class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;

    public TenantResolutionMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext)
    {
        // Strategy 1: JWT claim (for authenticated API requests)
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var schoolIdClaim = context.User.FindFirst("school_id");
            if (schoolIdClaim is not null)
            {
                await tenantContext.SetTenantAsync(schoolIdClaim.Value);
                await _next(context);
                return;
            }
        }

        // Strategy 2: Subdomain (for login page, before JWT exists)
        var host = context.Request.Host.Host; // "bangkok.grapeseed.com"
        var subdomain = host.Split('.')[0];    // "bangkok"
        if (!string.IsNullOrEmpty(subdomain) && subdomain != "www" && subdomain != "api")
        {
            await tenantContext.SetTenantBySubdomainAsync(subdomain);
            await _next(context);
            return;
        }

        // Strategy 3: X-School-Id header (for internal service-to-service calls)
        if (context.Request.Headers.TryGetValue("X-School-Id", out var headerSchoolId))
        {
            await tenantContext.SetTenantAsync(headerSchoolId.ToString());
            await _next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = "Could not determine school from request." });
    }
}
```

```csharp
// ─────────────────────────────────────────────────────────────────
// ITenantContext.cs — The school context that flows through the app
// ─────────────────────────────────────────────────────────────────
public interface ITenantContext
{
    string SchoolId { get; }
    SchoolInfo? SchoolInfo { get; }
    bool IsResolved { get; }
    Task SetTenantAsync(string schoolId);
    Task SetTenantBySubdomainAsync(string subdomain);
}

public class SchoolInfo
{
    public string SchoolId { get; set; } = string.Empty;
    public string SchoolName { get; set; } = string.Empty;       // "Bangkok English Academy"
    public string Subdomain { get; set; } = string.Empty;        // "bangkok"
    public string LicenseTier { get; set; } = "standard";        // "basic", "standard", "premium"
    public string TimeZone { get; set; } = "UTC";                 // "Asia/Bangkok"
    public int LicensedUnitsCount { get; set; } = 6;             // How many Grapeseed units they have
    public string PrimaryColor { get; set; } = "#1A6B3A";        // Grapeseed green by default
    public string LogoUrl { get; set; } = string.Empty;
    public string AwsRegion { get; set; } = "ap-southeast-1";    // Where this school's data lives
    public bool HasDedicatedDatabase { get; set; } = false;       // Premium schools only
    public Dictionary<string, bool> Features { get; set; } = new();
}
```

---

## 7. Propagating Tenant Context Through the Stack

Once resolved, `ITenantContext` must be available everywhere in the request pipeline. In ASP.NET Core, register it as **Scoped** — one instance per HTTP request. Every service in the same request gets the same `ITenantContext`.

```csharp
// ─────────────────────────────────────────────────────────────────
// Program.cs — Tenant context registration
// ─────────────────────────────────────────────────────────────────
builder.Services.AddScoped<ITenantContext, TenantContext>();
builder.Services.AddScoped<ITenantConnectionResolver, TenantConnectionResolver>();

// Register the DbContext as Scoped so it picks up the TenantContext
builder.Services.AddDbContext<GrapeseekDbContext>(ServiceLifetime.Scoped);

var app = builder.Build();

// Middleware order matters: tenant resolution before controllers
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
```

---

## 8. EF Core Global Query Filters — Automatic Data Isolation

This is the most critical safety mechanism for shared-table multi-tenancy. Without it, one developer forgetting to add `WHERE SchoolId = @schoolId` could expose all schools' data.

**EF Core Global Query Filters** automatically append a `WHERE` clause to every query on a filtered entity. Developers write normal queries — the filter is added invisibly and automatically.

```csharp
// ─────────────────────────────────────────────────────────────────
// Domain entities — all tenant-scoped entities inherit from TenantEntity
// ─────────────────────────────────────────────────────────────────
public abstract class TenantEntity
{
    public string SchoolId { get; set; } = string.Empty;
}

public class Student : TenantEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string CurrentUnit { get; set; } = "Unit 1";
    public string Level { get; set; } = "Beginner";
    public DateTime EnrolledAt { get; set; }
    public ICollection<LessonProgress> Progress { get; set; } = new List<LessonProgress>();
}

public class LessonAssignment : TenantEntity
{
    public int Id { get; set; }
    public string Unit { get; set; } = string.Empty;          // e.g., "Unit 3"
    public int LessonNumber { get; set; }
    public DateTime DueDate { get; set; }
    public bool IsRequired { get; set; }
}

public class LessonProgress : TenantEntity
{
    public int Id { get; set; }
    public int StudentId { get; set; }
    public string Unit { get; set; } = string.Empty;
    public int LessonNumber { get; set; }
    public bool IsCompleted { get; set; }
    public int ScorePercent { get; set; }
    public DateTime? CompletedAt { get; set; }
}
```

```csharp
// ─────────────────────────────────────────────────────────────────
// GrapeseekDbContext.cs — EF Core with automatic tenant isolation
// ─────────────────────────────────────────────────────────────────
public class GrapeseekDbContext : DbContext
{
    private readonly ITenantContext _tenantContext;

    public DbSet<Student> Students => Set<Student>();
    public DbSet<LessonAssignment> LessonAssignments => Set<LessonAssignment>();
    public DbSet<LessonProgress> LessonProgress => Set<LessonProgress>();

    public GrapeseekDbContext(
        DbContextOptions<GrapeseekDbContext> options,
        ITenantContext tenantContext) : base(options)
    {
        _tenantContext = tenantContext;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ✨ AUTOMATIC TENANT ISOLATION
        // These filters are added to every query on these entities.
        // A developer writing:
        //   _db.Students.ToListAsync()
        // Gets SQL:
        //   SELECT * FROM Students WHERE SchoolId = 'school-bkk-001'
        // They cannot accidentally forget the filter — it's always there.
        
        modelBuilder.Entity<Student>()
            .HasQueryFilter(s => s.SchoolId == _tenantContext.SchoolId);

        modelBuilder.Entity<LessonAssignment>()
            .HasQueryFilter(a => a.SchoolId == _tenantContext.SchoolId);

        modelBuilder.Entity<LessonProgress>()
            .HasQueryFilter(p => p.SchoolId == _tenantContext.SchoolId);

        // ─────────────────────────────────────────────────────────
        // PERFORMANCE CRITICAL: Index on SchoolId
        // Every query filters by SchoolId. Without an index, every
        // query scans the entire Students table (millions of rows
        // across all schools). With the index, queries only scan
        // the rows for the current school.
        // ─────────────────────────────────────────────────────────
        modelBuilder.Entity<Student>()
            .HasIndex(s => new { s.SchoolId, s.Email }).IsUnique();

        modelBuilder.Entity<Student>()
            .HasIndex(s => new { s.SchoolId, s.CurrentUnit });

        modelBuilder.Entity<LessonProgress>()
            .HasIndex(p => new { p.SchoolId, p.StudentId });

        modelBuilder.Entity<LessonAssignment>()
            .HasIndex(a => new { a.SchoolId, a.DueDate });
    }

    // ─────────────────────────────────────────────────────────────
    // Auto-set SchoolId on every INSERT
    // Developers don't need to remember to set entity.SchoolId —
    // it's set automatically from the current TenantContext.
    // ─────────────────────────────────────────────────────────────
    public override async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        foreach (var entry in ChangeTracker.Entries<TenantEntity>()
                     .Where(e => e.State == EntityState.Added))
        {
            if (string.IsNullOrEmpty(entry.Entity.SchoolId))
            {
                entry.Entity.SchoolId = _tenantContext.SchoolId;
            }
        }

        return await base.SaveChangesAsync(ct);
    }
}
```

```csharp
// ─────────────────────────────────────────────────────────────────
// StudentRepository.cs — Simple, safe queries thanks to global filters
// ─────────────────────────────────────────────────────────────────
public class StudentRepository : IStudentRepository
{
    private readonly GrapeseekDbContext _db;

    public StudentRepository(GrapeseekDbContext db) => _db = db;

    // This looks like "get all students" but the query filter automatically
    // makes it "get all students WHERE SchoolId = @currentSchool"
    // Generated SQL: SELECT * FROM Students WHERE SchoolId = 'school-bkk-001'
    public async Task<List<Student>> GetAllAsync()
        => await _db.Students.ToListAsync();

    // Generated SQL: SELECT * FROM Students 
    //               WHERE SchoolId = 'school-bkk-001' AND CurrentUnit = @unit
    public async Task<List<Student>> GetByUnitAsync(string unit)
        => await _db.Students
            .Where(s => s.CurrentUnit == unit)
            .Include(s => s.Progress)  // Also filtered by SchoolId automatically
            .ToListAsync();

    // Creating a new student: SchoolId is auto-set in SaveChangesAsync override
    public async Task<Student> CreateAsync(Student student)
    {
        _db.Students.Add(student);
        await _db.SaveChangesAsync();
        return student;
    }
}
```

> **⚠️ Admin Override:** Platform administrators need to query across all schools for monitoring. Use `.IgnoreQueryFilters()` very carefully, wrapped in a service that requires `PlatformAdmin` role:
> ```csharp
> // ADMIN ONLY — never expose this to school-level code paths
> var totalStudentsAllSchools = await _db.Students.IgnoreQueryFilters().CountAsync();
> ```

---

## 9. MediatR Pipeline Behavior for Tenant Context

This is where MediatR becomes extremely powerful for multi-tenancy. You can add a **pipeline behavior** that automatically validates and enriches the tenant context for every MediatR command and query — without touching the handlers themselves.

### What Is a MediatR Pipeline Behavior?

Think of it like ASP.NET Core middleware, but for MediatR requests. Instead of the middleware pipeline (HTTP Request → Middleware 1 → Middleware 2 → Controller), you have a MediatR pipeline: `IRequest → Behavior 1 → Behavior 2 → Handler`.

This is perfect for cross-cutting concerns: logging, validation, caching, and — in our case — tenant context enforcement.

```csharp
// Install: dotnet add package MediatR
// Install: dotnet add package MediatR.Extensions.Microsoft.DependencyInjection

// ─────────────────────────────────────────────────────────────────
// Program.cs — Register MediatR with pipeline behaviors
// ─────────────────────────────────────────────────────────────────
builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssemblyContaining<Program>();
    
    // Pipeline behaviors execute in registration order (outer to inner)
    cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
    cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(TenantValidationBehavior<,>));
    cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
    cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(CachingBehavior<,>));
});
```

```csharp
// ─────────────────────────────────────────────────────────────────
// Behaviors/TenantValidationBehavior.cs
// 
// This behavior runs BEFORE every MediatR handler.
// It ensures:
//   1. The tenant context is resolved
//   2. The school is active (not suspended)
//   3. The school has access to the requested feature
//   4. The tenant's SchoolId is stamped onto the request if it implements ITenantRequest
//
// Result: Every handler can TRUST that the tenant is valid.
// Handlers don't need to check this themselves — it's already done.
// ─────────────────────────────────────────────────────────────────

// Marker interface for requests that require tenant context
public interface ITenantRequest
{
    string SchoolId { get; set; }
}

public class TenantValidationBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<TenantValidationBehavior<TRequest, TResponse>> _logger;

    public TenantValidationBehavior(
        ITenantContext tenantContext,
        ILogger<TenantValidationBehavior<TRequest, TResponse>> logger)
    {
        _tenantContext = tenantContext;
        _logger = logger;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        // If the request requires tenant context, validate it
        if (request is ITenantRequest tenantRequest)
        {
            // Ensure tenant is resolved (middleware should have done this)
            if (!_tenantContext.IsResolved)
            {
                throw new UnauthorizedAccessException(
                    "School context could not be determined for this request.");
            }

            // Ensure the school is active and not suspended
            var school = _tenantContext.SchoolInfo;
            if (school is null || !school.IsActive)
            {
                throw new InvalidOperationException(
                    $"School '{_tenantContext.SchoolId}' is not active.");
            }

            // Stamp the SchoolId onto the request object itself
            // This makes the SchoolId available inside the handler
            // without needing to inject ITenantContext there too
            tenantRequest.SchoolId = _tenantContext.SchoolId;

            _logger.LogDebug("Tenant validated for request {RequestType}: SchoolId={SchoolId}",
                typeof(TRequest).Name, _tenantContext.SchoolId);
        }

        // Continue to the next behavior or the handler
        return await next();
    }
}
```

```csharp
// ─────────────────────────────────────────────────────────────────
// Example Query using MediatR
// ─────────────────────────────────────────────────────────────────

// The query — note it implements ITenantRequest
public record GetStudentProgressQuery(int StudentId) 
    : IRequest<StudentProgressResponse>, ITenantRequest
{
    // SchoolId is set automatically by TenantValidationBehavior
    // The handler doesn't need to worry about setting it
    public string SchoolId { get; set; } = string.Empty;
}

public record StudentProgressResponse(
    int StudentId,
    string StudentName,
    string CurrentUnit,
    int CompletedLessons,
    double AverageScore);

// The handler — clean and focused on business logic only
// No need to validate tenant, no need to inject ITenantContext
public class GetStudentProgressQueryHandler 
    : IRequestHandler<GetStudentProgressQuery, StudentProgressResponse>
{
    private readonly IStudentRepository _students;
    private readonly IProgressRepository _progress;

    public GetStudentProgressQueryHandler(
        IStudentRepository students, 
        IProgressRepository progress)
    {
        _students = students;
        _progress = progress;
    }

    public async Task<StudentProgressResponse> Handle(
        GetStudentProgressQuery request, 
        CancellationToken cancellationToken)
    {
        // The SchoolId is already set by TenantValidationBehavior.
        // The repository's EF Core DbContext has GlobalQueryFilters
        // that automatically scope all queries to this school.
        // So GetByIdAsync will ONLY return students in the current school.
        var student = await _students.GetByIdAsync(request.StudentId)
            ?? throw new NotFoundException($"Student {request.StudentId} not found.");

        var completedLessons = await _progress.GetCompletedCountAsync(request.StudentId);
        var avgScore = await _progress.GetAverageScoreAsync(request.StudentId);

        return new StudentProgressResponse(
            student.Id,
            student.Name,
            student.CurrentUnit,
            completedLessons,
            avgScore);
    }
}
```

```csharp
// ─────────────────────────────────────────────────────────────────
// Controller using MediatR — extremely clean
// ─────────────────────────────────────────────────────────────────
[ApiController]
[Route("api/students")]
public class StudentsController : ControllerBase
{
    private readonly IMediator _mediator;

    public StudentsController(IMediator mediator) => _mediator = mediator;

    [HttpGet("{studentId:int}/progress")]
    public async Task<ActionResult<StudentProgressResponse>> GetProgress(int studentId)
    {
        // The controller doesn't know or care about tenants.
        // The MediatR pipeline handles it.
        var result = await _mediator.Send(new GetStudentProgressQuery(studentId));
        return Ok(result);
    }
}
```

This is the beauty of MediatR pipeline behaviors: the concern of "is the tenant valid?" is handled **once**, in one place, for every request in the system. Handlers stay small and focused purely on business logic.

---

## 10. Row-Level Security in PostgreSQL and SQL Server

EF Core global query filters protect at the application layer. For additional defense, enforce isolation at the **database layer** — so even raw SQL queries or direct DB connections can't bypass the tenant filter.

### PostgreSQL Row-Level Security (RLS)

```sql
-- ─────────────────────────────────────────────────────────────────
-- PostgreSQL RLS for Grapeseed
-- ─────────────────────────────────────────────────────────────────

-- Step 1: Enable RLS
ALTER TABLE "Students" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "LessonProgress" ENABLE ROW LEVEL SECURITY;

-- Step 2: Create policy — only see rows for the current school
CREATE POLICY grapeseed_school_isolation ON "Students"
    USING ("SchoolId" = current_setting('grapeseed.current_school_id', true));

CREATE POLICY grapeseed_school_isolation ON "LessonProgress"
    USING ("SchoolId" = current_setting('grapeseed.current_school_id', true));

-- Step 3: Your application sets this variable at the start of each request
-- (see C# code below)
```

```csharp
// ─────────────────────────────────────────────────────────────────
// Setting the PostgreSQL session variable from EF Core
// ─────────────────────────────────────────────────────────────────
public class GrapeseekDbContext : DbContext
{
    // ... (see earlier code) ...

    // Call this before any query execution to activate RLS
    public async Task ApplyTenantSessionPolicyAsync()
    {
        if (_tenantContext.IsResolved)
        {
            // SET LOCAL applies only to the current transaction — safe for pooled connections
            await Database.ExecuteSqlRawAsync(
                "SELECT set_config('grapeseed.current_school_id', {0}, true)",
                _tenantContext.SchoolId
            );
        }
    }
}
```

### SQL Server Row-Level Security

Grapeseed's Analytics Service and some enterprise integrations use SQL Server. SQL Server supports RLS through **Security Policies**:

```sql
-- ─────────────────────────────────────────────────────────────────
-- SQL Server RLS for Grapeseed Analytics DB
-- ─────────────────────────────────────────────────────────────────

-- Step 1: Create a schema for security functions
CREATE SCHEMA Security;
GO

-- Step 2: Create the predicate function
-- This function returns 1 (allow) if the row's SchoolId matches
-- the SESSION_CONTEXT value set by the application
CREATE FUNCTION Security.GrapeseekTenantFilter(@SchoolId NVARCHAR(100))
    RETURNS TABLE
WITH SCHEMABINDING
AS
    RETURN SELECT 1 AS FilterResult
    WHERE @SchoolId = CAST(SESSION_CONTEXT(N'SchoolId') AS NVARCHAR(100));
GO

-- Step 3: Apply the security policy to the ReportData table
CREATE SECURITY POLICY GrapeseekTenantPolicy
    ADD FILTER PREDICATE Security.GrapeseekTenantFilter(SchoolId) ON dbo.StudentReportData,
    ADD BLOCK PREDICATE Security.GrapeseekTenantFilter(SchoolId) ON dbo.StudentReportData
WITH (STATE = ON);
GO
```

```csharp
// ─────────────────────────────────────────────────────────────────
// Setting SQL Server SESSION_CONTEXT from EF Core
// ─────────────────────────────────────────────────────────────────
public class AnalyticsDbContext : DbContext
{
    private readonly ITenantContext _tenantContext;

    public async Task ApplyTenantSessionContextAsync()
    {
        if (_tenantContext.IsResolved)
        {
            // Set SESSION_CONTEXT for SQL Server RLS
            await Database.ExecuteSqlRawAsync(
                "EXEC sp_set_session_context N'SchoolId', {0}",
                _tenantContext.SchoolId
            );
        }
    }
}
```

With both EF Core global filters AND database-level RLS enabled, you have **defense in depth**: two independent, autonomous layers of isolation. A bug in one layer cannot compromise the other.

---

## 11. Tenant-Aware Feature Flags and Configuration

Grapeseed has multiple license tiers. Feature flags control what each school can access:

```csharp
// ─────────────────────────────────────────────────────────────────
// GrapeseekFeatures.cs — Centralized feature flag constants
// ─────────────────────────────────────────────────────────────────
public static class GrapeseekFeatures
{
    // Available to all tiers
    public const string CoreLessons = "core_lessons";
    public const string BasicQuizzes = "basic_quizzes";
    public const string TeacherDashboard = "teacher_dashboard";

    // Standard tier and above
    public const string ProgressReports = "progress_reports";
    public const string ParentPortal = "parent_portal";
    public const string SisIntegration = "sis_integration";       // Student Information System

    // Premium tier only
    public const string LiveCoaching = "live_coaching";
    public const string AiPronunciation = "ai_pronunciation";
    public const string AdvancedAnalytics = "advanced_analytics";
    public const string CustomUnitPacing = "custom_unit_pacing";
}

// ─────────────────────────────────────────────────────────────────
// ITenantFeatureService.cs
// ─────────────────────────────────────────────────────────────────
public interface ITenantFeatureService
{
    bool IsEnabled(string featureName);
    T GetSetting<T>(string settingKey, T defaultValue);
}

public class TenantFeatureService : ITenantFeatureService
{
    private readonly ITenantContext _tenant;

    public bool IsEnabled(string featureName)
    {
        var school = _tenant.SchoolInfo;
        if (school is null) return false;
        return school.Features.TryGetValue(featureName, out var enabled) && enabled;
    }

    public T GetSetting<T>(string settingKey, T defaultValue)
    {
        // Return the school-specific setting, or fall back to the platform default
        // Settings stored in ElastiCache keyed by "tenant-settings:{schoolId}:{settingKey}"
        // ...
        return defaultValue;
    }
}
```

```csharp
// Using features in a controller:
[HttpPost("coaching/schedule")]
public async Task<IActionResult> ScheduleCoachingSession(ScheduleCoachingRequest request)
{
    if (!_features.IsEnabled(GrapeseekFeatures.LiveCoaching))
    {
        return StatusCode(403, new
        {
            Error = "Live Coaching is not included in your current Grapeseed license.",
            UpgradeUrl = "https://grapeseed.com/upgrade"
        });
    }
    
    var result = await _mediator.Send(new ScheduleCoachingCommand(request));
    return Ok(result);
}
```

---

## 12. Storing Tenant Data on AWS

### Tenant Configuration in ElastiCache

School configurations (name, logo, features, timezone) are read on every request. Cache them aggressively:

```csharp
// TenantContext.cs — Loading school info with Redis caching
public async Task SetTenantAsync(string schoolId)
{
    SchoolId = schoolId;

    // Try ElastiCache first
    var cacheKey = $"school-info:{schoolId}";
    var cached = await _cache.GetStringAsync(cacheKey);
    if (cached is not null)
    {
        SchoolInfo = JsonSerializer.Deserialize<SchoolInfo>(cached);
        IsResolved = true;
        return;
    }

    // Load from RDS
    SchoolInfo = await _schoolRepository.GetSchoolInfoAsync(schoolId);
    IsResolved = SchoolInfo is not null;

    if (SchoolInfo is not null)
    {
        // Cache for 30 minutes — school settings change rarely
        await _cache.SetStringAsync(
            cacheKey,
            JsonSerializer.Serialize(SchoolInfo),
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30)
            });
    }
}
```

### Tenant Files in S3

Student certificates, lesson PDFs, and audio recordings are stored in S3 with **school-level prefixes**:

```
S3 Bucket: grapeseed-content-prod
├── schools/
│   ├── school-bkk-001/
│   │   ├── certificates/student-123-unit3.pdf
│   │   ├── recordings/student-123-unit2-lesson5.mp3
│   │   └── teacher-uploads/custom-lesson-template.pdf
│   │
│   └── school-tor-002/
│       ├── certificates/student-456-unit6.pdf
│       └── recordings/student-456-unit5-lesson3.mp3
│
└── shared/
    ├── grapeseed-lessons/unit1/lesson1/video.mp4
    └── grapeseed-lessons/unit1/lesson1/worksheet.pdf
```

S3 bucket policies and IAM roles ensure that Grapeseed's backend can access all paths, but tenant-facing pre-signed URLs are scoped to the specific school's prefix.

---

## 13. The Grapeseed Scenario

Let's trace a complete request through the multi-tenant Grapeseed system:

```
Scenario: A student at Bangkok English Academy opens their Grapeseed dashboard.

1. HTTP Request arrives:
   GET https://bangkok.grapeseed.com/api/students/me/progress
   Authorization: Bearer eyJhbGciOiJSUzI1NiJ9... (JWT)
   
   JWT payload (decoded):
   { "sub": "student-789", "school_id": "school-bkk-001", "role": "student" }

2. TenantResolutionMiddleware runs:
   - Reads JWT claim: school_id = "school-bkk-001"
   - Calls TenantContext.SetTenantAsync("school-bkk-001")
   - ElastiCache HIT: SchoolInfo loaded in ~1ms
     { Name: "Bangkok English Academy", Tier: "standard", 
       TimeZone: "Asia/Bangkok", LicensedUnits: 8 }
   - ITenantContext.IsResolved = true

3. Controller calls _mediator.Send(new GetStudentProgressQuery(789)):

4. MediatR Pipeline:
   ├── LoggingBehavior: logs "Handling GetStudentProgressQuery for school-bkk-001"
   ├── TenantValidationBehavior:
   │     - Confirms school-bkk-001 is active ✅
   │     - Stamps request.SchoolId = "school-bkk-001"
   └── Handler executes:

5. StudentRepository.GetByIdAsync(789) executes:
   Generated SQL:
   SELECT * FROM "Students"
   WHERE "SchoolId" = 'school-bkk-001'   ← added by Global Query Filter
   AND "Id" = 789
   
   Result: Siriporn's profile (Bangkok school). 
   Student 456 from Toronto: never touched. Never visible.

6. Response sent. 
   Siriporn sees her Unit 3 progress in Bangkok English Academy's branding.
   A student at Toronto English School, in a parallel request, sees Toronto's data.

All isolation is enforced architecturally.
No bugs required to maintain it. It simply cannot be breached by accident.
```

---

## 14. Decision Guide

| Criteria | Model 1 (Separate DB) | Model 2 (Separate Schema) | Model 3 (Shared Tables) |
|----------|-----------------------|---------------------------|-------------------------|
| Max practical schools | ~50-100 | ~500-1,000 | Unlimited |
| Data isolation | Maximum | High | Good (with EF filters + RLS) |
| AWS cost | Highest | Medium | Lowest |
| Migration complexity | Run per tenant | Run per schema | Run once |
| Data residency (PDPA, GDPR) | ✅ Easy | ⚠️ Possible | ❌ Complex |
| Per-school backup/restore | ✅ Easy | ✅ Easy | ❌ Complex |
| Best for | Enterprise districts | Mid-market | Standard schools |

---

## 15. Summary and Key Takeaways

### Core Concepts

| Concept | One-Line Summary |
|---------|-----------------|
| Multi-tenancy | One application, many isolated schools |
| Model 3 (Shared Tables) | Most cost-efficient; requires rigorous SchoolId scoping |
| EF Core Global Query Filters | Automatically adds WHERE SchoolId = @id to every query |
| Auto-set SchoolId on INSERT | SaveChangesAsync override ensures new rows always have SchoolId |
| MediatR TenantValidationBehavior | Validates and stamps tenant context before every handler runs |
| PostgreSQL RLS | Database-enforced row filtering (defense in depth) |
| SQL Server Security Policy | Same concept for the analytics SQL Server database |
| Feature flags | Per-school license tier control via dictionary in SchoolInfo |
| ElastiCache | Cache SchoolInfo for 30 minutes to avoid RDS on every request |
| S3 school prefixes | Tenant-scoped file storage with IAM-controlled access |

### The Golden Rules of Multi-Tenancy

1. **Resolve the tenant first** — The very first middleware action must be tenant resolution.
2. **Make isolation automatic** — EF Core filters + DB RLS + MediatR behaviors. Isolation by architecture, not discipline.
3. **Index `SchoolId`** — Every single query filters by `SchoolId`. Without a composite index, you'll do full table scans.
4. **Cache school info** — `SchoolInfo` is read on every request. Cache it in ElastiCache for 30 minutes.
5. **Test for leakage** — Write explicit integration tests that verify school A cannot see school B's data, even with direct repository calls.

*→ Continue to: [Chapter 3 — High Load Systems](./book_ch3_high_load_systems.md)*

---

*Chapter 2 Complete · 15 sections · Multi-Tenant Architecture on AWS*
