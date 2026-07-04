# Chapter 2: Multi-Tenant Architecture

> **Advanced Architecture Pattern · Data Isolation · Enterprise SaaS**
> *"Multi-tenancy is the art of serving many customers from one system, while making each customer feel like they have the system all to themselves."*

---

## Table of Contents

1. [Introduction — One Platform, Many Schools](#1-introduction)
2. [What Is Multi-Tenancy?](#2-what-is-multi-tenancy)
3. [The Three Tenancy Models](#3-tenancy-models)
4. [Choosing the Right Model for LinguaLearn](#4-choosing-the-right-model)
5. [Tenant Isolation — Beyond Just Data](#5-tenant-isolation)
6. [Resolving the Tenant — Who Is Calling?](#6-resolving-the-tenant)
7. [Propagating Tenant Context Through the Stack](#7-propagating-tenant-context)
8. [EF Core Global Query Filters — Automatic Data Isolation](#8-ef-core-global-query-filters)
9. [Row-Level Security in the Database](#9-row-level-security)
10. [Tenant-Aware Feature Flags and Configuration](#10-tenant-aware-features)
11. [The Education Platform Scenario](#11-education-platform-scenario)
12. [Decision Guide — Which Model Should You Choose?](#12-decision-guide)
13. [Summary and Key Takeaways](#13-summary)

---

## 1. Introduction — One Platform, Many Schools

Imagine you work at an apartment management company. You manage a hundred different apartment buildings. Each building has its own residents, its own rules, its own front desk staff, and its own maintenance requests.

Now, you could hire a separate management team for each building. That would be simple — each team only deals with their one building, and there's zero chance of confusing one building's residents with another's. But it's also wildly expensive and inefficient.

Instead, your company manages all buildings from one central office, using shared staff, shared software, and shared processes. Each building's residents have their own private account, can only see their own building's information, and never even know that other buildings exist in the system. They each think they have a dedicated property manager.

**This is multi-tenancy.** In software terms:
- Each school (or company) is a **tenant**
- The central office is your **application**
- Each tenant gets a completely isolated experience on the same shared infrastructure

In LinguaLearn's case, we have hundreds of schools worldwide. Each school is a tenant that:
- Has their own teachers and students
- Has their own lesson content and curriculum
- Has their own branding (logo, colors)
- Has their own configuration (grading rules, language settings)
- **Must never see any data from any other school**

Building this correctly is the focus of this chapter.

---

## 2. What Is Multi-Tenancy?

**Multi-tenancy** is a software architecture where a single instance of an application serves multiple tenants (customers/organizations), with each tenant's data and configuration isolated from all others.

### The Core Promise of Multi-Tenancy

No matter what happens in the application, these three guarantees must hold:

```
┌─────────────────────────────────────────────────────────────────┐
│              The Multi-Tenancy Guarantees                        │
│                                                                   │
│  1. DATA ISOLATION                                               │
│     School A can never read or write School B's data.           │
│     Not through bugs. Not through misconfiguration.              │
│     Not through clever API manipulation.                         │
│                                                                   │
│  2. CONFIGURATION ISOLATION                                      │
│     Changing School A's settings never affects School B.         │
│     Each school can independently configure their experience.    │
│                                                                   │
│  3. PERFORMANCE ISOLATION                                        │
│     School A generating a huge report should not slow down       │
│     the experience for students in School B.                     │
└─────────────────────────────────────────────────────────────────┘
```

### The Business Case

Why go multi-tenant instead of deploying separate applications for each school?

| Concern | Separate Deployments | Multi-Tenant |
|---------|---------------------|--------------|
| Infra Cost | Very high (N servers for N tenants) | Low (shared resources) |
| Deployment | N deployments per release | 1 deployment for all |
| Bug fixes | Fix N times | Fix once |
| Scaling | Scale each tenant separately | Scale the whole platform |
| Onboarding new tenant | Set up new server, deploy, configure | Add a row to the DB |
| Security updates | Apply to N systems | Apply once |

For a business serving 500 schools, multi-tenancy can reduce infrastructure costs by 80-90% and operational complexity by even more.

---

## 3. The Three Tenancy Models

There is no single "correct" way to implement multi-tenancy. There are three fundamental approaches, each with different tradeoffs. Let's understand all three.

### Model 1: Separate Database per Tenant (Siloed)

Each tenant gets their own completely independent database.

```
┌─────────────────────────────────────────────────────────────────┐
│              Model 1: Separate Database per Tenant               │
│                                                                   │
│   Application (shared)                                           │
│         │                                                         │
│         ├──► Database: school_tokyo_db      ← School Tokyo       │
│         ├──► Database: school_london_db     ← School London      │
│         ├──► Database: school_sydney_db     ← School Sydney      │
│         └──► Database: school_newyork_db   ← School New York    │
└─────────────────────────────────────────────────────────────────┘
```

**✅ Pros:**
- Maximum data isolation — databases are physically separate
- Easy to give a tenant their own database backup and restore
- Can move a high-value tenant to better hardware
- Compliance with strict data residency laws (School Tokyo data stays in Japan)

**❌ Cons:**
- Expensive — each database uses resources even when idle
- Schema migrations must run N times (once per tenant database)
- Connection pool complexity — you need different connection strings for different tenants
- Not practical beyond ~100 tenants

**Best for:** Very high-value clients, regulated industries (healthcare, government), or when data residency laws mandate it.

---

### Model 2: Shared Database, Separate Schemas

One database, but each tenant gets their own schema (a namespace within the database).

```
┌─────────────────────────────────────────────────────────────────┐
│           Model 2: Shared Database, Separate Schemas             │
│                                                                   │
│   One Database: lingualearn_db                                   │
│         │                                                         │
│         ├── Schema: tokyo_school                                 │
│         │     ├── students table                                 │
│         │     ├── lessons table                                  │
│         │     └── progress table                                 │
│         │                                                         │
│         ├── Schema: london_school                                │
│         │     ├── students table                                 │
│         │     ├── lessons table                                  │
│         │     └── progress table                                 │
│         │                                                         │
│         └── Schema: sydney_school                               │
│               ├── students table                                 │
│               ├── lessons table                                  │
│               └── progress table                                 │
└─────────────────────────────────────────────────────────────────┘
```

**✅ Pros:**
- Good data isolation — schemas act as namespaces
- Easier to export one tenant's data
- Can still run tenant-specific queries easily

**❌ Cons:**
- Schema migrations must still run N times
- Not practical beyond ~1,000 tenants
- Supported differently across databases (PostgreSQL supports it well; MySQL less so)

**Best for:** Mid-range SaaS with dozens to hundreds of tenants, especially when data export per tenant is common.

---

### Model 3: Shared Database, Shared Tables (Discriminator Column)

One database, one set of tables for all tenants. Every row has a `TenantId` column that identifies which tenant it belongs to.

```
┌─────────────────────────────────────────────────────────────────┐
│          Model 3: Shared Database, Shared Tables                 │
│                                                                   │
│   One Database: lingualearn_db                                   │
│   One Schema: public                                             │
│                                                                   │
│   Students Table:                                                │
│   ┌──────────┬────────────┬──────────────────┬───────────────┐  │
│   │ Id       │ TenantId   │ Name             │ Email         │  │
│   ├──────────┼────────────┼──────────────────┼───────────────┤  │
│   │ 1        │ tokyo      │ Yuki Tanaka      │ yuki@...      │  │
│   │ 2        │ london     │ Emma Watson      │ emma@...      │  │
│   │ 3        │ tokyo      │ Kenji Sato       │ kenji@...     │  │
│   │ 4        │ sydney     │ Jack Ryan        │ jack@...      │  │
│   └──────────┴────────────┴──────────────────┴───────────────┘  │
│                                                                   │
│   Every query MUST include WHERE TenantId = 'current_tenant'    │
└─────────────────────────────────────────────────────────────────┘
```

**✅ Pros:**
- Lowest infrastructure cost — one database for everything
- Schema migrations run once for all tenants
- Efficient for thousands or even millions of tenants
- Simplest operational model

**❌ Cons:**
- **Catastrophic if the TenantId filter is ever forgotten** — one bug could expose all tenants' data
- "Noisy neighbor" problem — one tenant's heavy queries can slow others
- Cross-tenant reporting (for platform admins) requires careful design

**Best for:** High-volume SaaS with thousands of tenants, where cost efficiency matters most.

> **LinguaLearn's Choice:** We'll use **Model 3** (shared tables with TenantId) as our primary model in this chapter. It's the most common in real-world SaaS platforms and the most important to understand. We'll show how to make the TenantId filter automatic so it can never be forgotten.

---

## 4. Choosing the Right Model for LinguaLearn

LinguaLearn serves schools worldwide. Here's how to think about the choice:

```
                Start Here
                     │
         ┌───────────┴───────────┐
         │  Does the school have │
         │  strict data residency│
         │  requirements?        │
         └───────────┬───────────┘
                     │
          ┌──────────┴──────────┐
         YES                    NO
          │                      │
   Separate Database        ┌────┴────────────────┐
   per Tenant (Model 1)     │ Is this a very high  │
   or per Region            │ value enterprise     │
                            │ client (paying 10x)? │
                            └────┬────────────────┘
                                 │
                      ┌──────────┴──────────┐
                     YES                    NO
                      │                      │
               Separate Schema          Shared Tables
               per Tenant (Model 2)     with TenantId
                                        (Model 3) ← Most schools
```

**Decision for LinguaLearn:**
- Standard schools → **Model 3** (shared tables, efficient, cheap)
- Premium enterprise schools with compliance requirements → **Model 1** (own database)
- This is called a **hybrid approach** — most SaaS platforms end up here

---

## 5. Tenant Isolation — Beyond Just Data

When people think about multi-tenancy, they usually think about database isolation. But true tenant isolation is much broader:

### 1. Data Isolation
The obvious one — School A's students are never visible to School B's queries.

### 2. Configuration Isolation
Each school can have its own settings:
- Lesson difficulty progression rules
- Grading scale (A/B/C vs 1-10 vs pass/fail)
- Enabled languages (English only, or English+Mandarin)
- Feature flags (is the quiz module enabled for this school?)
- Notification preferences

### 3. Feature Isolation
Premium schools might have access to features that free-tier schools don't:
- Live video classes
- AI-powered pronunciation feedback
- Advanced analytics dashboard
- Custom lesson builder

### 4. Branding Isolation
Each school sees their own branding:
- School logo and colors
- Custom domain (`learn.tokyoenglishschool.com`)
- Custom email footer with school contact info

### 5. Performance Isolation
One school running a massive report export shouldn't slow down other schools' lesson delivery. This can be achieved through:
- Rate limiting per tenant
- Separate job queues for heavy operations
- Tenant-aware resource budgets

---

## 6. Resolving the Tenant — Who Is Calling?

Before your application can enforce any isolation, it needs to answer: **which tenant is making this request?**

This is called **tenant resolution**. There are several strategies:

### Strategy A: Subdomain-Based Resolution

Each school gets their own subdomain:
- `tokyo.lingualearn.com`
- `london.lingualearn.com`
- `sydney.lingualearn.com`

The middleware extracts the subdomain from the incoming request's `Host` header.

**✅ Best for:** User-facing apps where branding matters. Each school sees a custom URL.

### Strategy B: JWT Claim-Based Resolution

After the user logs in, the JWT access token contains a `tenant_id` claim:

```json
{
  "sub": "user-456",
  "email": "yuki@tokyoschool.com",
  "tenant_id": "tokyo-school-001",
  "role": "student",
  "exp": 1751234567
}
```

Every API request carries this JWT. The middleware reads the `tenant_id` from the validated token.

**✅ Best for:** API-first systems. Secure because the tenant ID is inside a signed token — users can't fake it.

### Strategy C: API Key / Header-Based Resolution

Service-to-service calls include a `X-Tenant-Id` header. Useful for internal microservices.

**✅ Best for:** Internal service communication where the calling service is trusted.

```csharp
// ─────────────────────────────────────────────────────────────────
// TenantResolutionMiddleware.cs
// Resolves tenant from JWT claims (the recommended approach for LinguaLearn)
// ─────────────────────────────────────────────────────────────────
public class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;

    public TenantResolutionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext)
    {
        // Strategy 1: Try to resolve from JWT claim (for authenticated endpoints)
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var tenantIdClaim = context.User.FindFirst("tenant_id");
            if (tenantIdClaim is not null)
            {
                tenantContext.SetTenant(tenantIdClaim.Value);
                await _next(context);
                return;
            }
        }

        // Strategy 2: Try to resolve from subdomain (for login page)
        var host = context.Request.Host.Host; // e.g., "tokyo.lingualearn.com"
        var parts = host.Split('.');
        if (parts.Length >= 3) // subdomain.domain.tld
        {
            var subdomain = parts[0]; // "tokyo"
            tenantContext.SetTenantBySubdomain(subdomain);
            await _next(context);
            return;
        }

        // Strategy 3: Try X-Tenant-Id header (for service-to-service)
        if (context.Request.Headers.TryGetValue("X-Tenant-Id", out var headerTenantId))
        {
            tenantContext.SetTenant(headerTenantId.ToString());
            await _next(context);
            return;
        }

        // If we can't determine the tenant, return 400 Bad Request
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("Unable to determine tenant from request.");
    }
}
```

```csharp
// ─────────────────────────────────────────────────────────────────
// ITenantContext.cs — The tenant context that flows through the app
// ─────────────────────────────────────────────────────────────────
public interface ITenantContext
{
    string TenantId { get; }
    TenantInfo? TenantInfo { get; }
    void SetTenant(string tenantId);
    void SetTenantBySubdomain(string subdomain);
    bool IsResolved { get; }
}

public class TenantContext : ITenantContext
{
    private readonly ITenantRepository _tenantRepository;

    public string TenantId { get; private set; } = string.Empty;
    public TenantInfo? TenantInfo { get; private set; }
    public bool IsResolved { get; private set; }

    public TenantContext(ITenantRepository tenantRepository)
    {
        _tenantRepository = tenantRepository;
    }

    public void SetTenant(string tenantId)
    {
        TenantId = tenantId;
        // Optionally load full tenant info from cache/DB
        TenantInfo = _tenantRepository.GetTenantInfo(tenantId);
        IsResolved = true;
    }

    public void SetTenantBySubdomain(string subdomain)
    {
        // Look up the tenantId from the subdomain in a subdomain mapping table
        var tenantId = _tenantRepository.GetTenantIdBySubdomain(subdomain);
        if (tenantId is not null)
        {
            SetTenant(tenantId);
        }
    }
}

// ─────────────────────────────────────────────────────────────────
// TenantInfo.cs — Rich information about the tenant
// ─────────────────────────────────────────────────────────────────
public class TenantInfo
{
    public string TenantId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;           // "Tokyo English Academy"
    public string Subdomain { get; set; } = string.Empty;     // "tokyo"
    public string PlanType { get; set; } = string.Empty;      // "premium", "standard", "free"
    public string TimeZone { get; set; } = "UTC";              // "Asia/Tokyo"
    public string DefaultLanguage { get; set; } = "en";
    public string LogoUrl { get; set; } = string.Empty;
    public string PrimaryColor { get; set; } = "#007BFF";
    public bool IsActive { get; set; } = true;
    public Dictionary<string, bool> Features { get; set; } = new(); // Feature flags
}
```

---

## 7. Propagating Tenant Context Through the Stack

Once the middleware resolves the tenant, every part of the application needs access to `ITenantContext`. In ASP.NET Core, this is done through **Dependency Injection with a Scoped lifetime**.

```csharp
// ─────────────────────────────────────────────────────────────────
// Program.cs — Registering tenant context as Scoped
// ─────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

// TenantContext is SCOPED: one instance per HTTP request
// This means every service injected in the same request gets the SAME TenantContext
builder.Services.AddScoped<ITenantContext, TenantContext>();
builder.Services.AddScoped<ITenantRepository, TenantRepository>();

// ... other services ...

var app = builder.Build();

// Register middleware BEFORE routing so tenant is resolved for all requests
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
```

Now any service can get the current tenant just by injecting `ITenantContext`:

```csharp
// ─────────────────────────────────────────────────────────────────
// LessonService.cs — Tenant-aware business logic
// ─────────────────────────────────────────────────────────────────
public class LessonService : ILessonService
{
    private readonly ILessonRepository _repository;
    private readonly ITenantContext _tenantContext;

    public LessonService(ILessonRepository repository, ITenantContext tenantContext)
    {
        _repository = repository;
        _tenantContext = tenantContext;
    }

    public async Task<IEnumerable<Lesson>> GetAllLessonsAsync()
    {
        // The tenantContext.TenantId is automatically resolved from the request.
        // Every call to the repository will be scoped to this tenant.
        return await _repository.GetLessonsForTenantAsync(_tenantContext.TenantId);
    }
}
```

---

## 8. EF Core Global Query Filters — Automatic Data Isolation

Here is the most dangerous aspect of the shared-table model: **every database query must include a `WHERE TenantId = @currentTenantId` clause.** If a developer forgets to add this filter — even once — they could expose all tenants' data to the wrong school.

This is a human error waiting to happen. The solution is to make the filter **automatic** using **EF Core Global Query Filters**. This feature lets you define a filter at the entity level that is automatically applied to every query involving that entity. Developers don't need to think about it.

```csharp
// ─────────────────────────────────────────────────────────────────
// Entities — all tenant-scoped entities have TenantId
// ─────────────────────────────────────────────────────────────────

// Base class for all tenant-scoped entities
public abstract class TenantEntity
{
    public string TenantId { get; set; } = string.Empty;
}

public class Student : TenantEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Level { get; set; } = "Beginner";
    public DateTime EnrolledAt { get; set; }
    
    // Navigation properties
    public ICollection<LessonProgress> Progress { get; set; } = new List<LessonProgress>();
}

public class Lesson : TenantEntity
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int Unit { get; set; }
    public string Level { get; set; } = "Beginner";
    public string VideoId { get; set; } = string.Empty;
}

public class LessonProgress : TenantEntity
{
    public int Id { get; set; }
    public int StudentId { get; set; }
    public int LessonId { get; set; }
    public bool IsCompleted { get; set; }
    public int ScorePercent { get; set; }
    public DateTime CompletedAt { get; set; }
}
```

```csharp
// ─────────────────────────────────────────────────────────────────
// ApplicationDbContext.cs — The HEART of multi-tenant isolation
// ─────────────────────────────────────────────────────────────────
public class ApplicationDbContext : DbContext
{
    private readonly ITenantContext _tenantContext;

    public DbSet<Student> Students => Set<Student>();
    public DbSet<Lesson> Lessons => Set<Lesson>();
    public DbSet<LessonProgress> LessonProgress => Set<LessonProgress>();

    public ApplicationDbContext(
        DbContextOptions<ApplicationDbContext> options,
        ITenantContext tenantContext) : base(options)
    {
        _tenantContext = tenantContext;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ✨ THE MAGIC: Global Query Filters
        // This filter is automatically applied to EVERY query on these entities.
        // Developers cannot forget it — it's always there.
        
        modelBuilder.Entity<Student>()
            .HasQueryFilter(s => s.TenantId == _tenantContext.TenantId);

        modelBuilder.Entity<Lesson>()
            .HasQueryFilter(l => l.TenantId == _tenantContext.TenantId);

        modelBuilder.Entity<LessonProgress>()
            .HasQueryFilter(p => p.TenantId == _tenantContext.TenantId);

        // Add indexes — CRITICAL for performance with shared tables.
        // All queries filter by TenantId, so it must be indexed!
        modelBuilder.Entity<Student>()
            .HasIndex(s => new { s.TenantId, s.Email }).IsUnique();

        modelBuilder.Entity<Lesson>()
            .HasIndex(l => new { l.TenantId, l.Unit, l.Level });

        modelBuilder.Entity<LessonProgress>()
            .HasIndex(p => new { p.TenantId, p.StudentId });
    }

    // ─────────────────────────────────────────────────────────────
    // Override SaveChangesAsync to auto-set TenantId on new entities
    // This ensures TenantId is always set correctly on INSERT
    // ─────────────────────────────────────────────────────────────
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        // Find all newly added TenantEntity objects
        var tenantEntities = ChangeTracker
            .Entries<TenantEntity>()
            .Where(e => e.State == EntityState.Added);

        // Automatically assign the current tenant's ID
        foreach (var entry in tenantEntities)
        {
            if (string.IsNullOrEmpty(entry.Entity.TenantId))
            {
                entry.Entity.TenantId = _tenantContext.TenantId;
            }
        }

        return await base.SaveChangesAsync(cancellationToken);
    }
}
```

With this setup, here's what happens when a developer writes a query:

```csharp
// ─────────────────────────────────────────────────────────────────
// StudentRepository.cs — Simple queries, automatic tenant isolation
// ─────────────────────────────────────────────────────────────────
public class StudentRepository : IStudentRepository
{
    private readonly ApplicationDbContext _db;

    public StudentRepository(ApplicationDbContext db)
    {
        _db = db;
    }

    // This LOOKS like it's fetching all students, but the Global Query Filter
    // automatically adds "WHERE TenantId = @currentTenant" to the SQL.
    // The developer doesn't need to think about it!
    public async Task<List<Student>> GetAllStudentsAsync()
    {
        // Generated SQL: SELECT * FROM Students WHERE TenantId = 'tokyo-school-001'
        return await _db.Students.ToListAsync();
    }

    // Same here — only this tenant's students will be searched
    public async Task<Student?> FindByEmailAsync(string email)
    {
        // Generated SQL: SELECT TOP 1 * FROM Students 
        //                WHERE TenantId = 'tokyo-school-001' AND Email = @email
        return await _db.Students.FirstOrDefaultAsync(s => s.Email == email);
    }

    // Even complex queries are automatically scoped
    public async Task<List<Student>> GetStudentsByLevelAsync(string level)
    {
        return await _db.Students
            .Where(s => s.Level == level)
            .Include(s => s.Progress)  // Related entities are also filtered!
            .OrderBy(s => s.Name)
            .ToListAsync();
    }
}
```

> **⚠️ Important Edge Case:** Sometimes platform administrators (not school users) need to query across all tenants — for example, to generate a platform-wide analytics report. EF Core allows you to bypass the query filter with `.IgnoreQueryFilters()`:
>
> ```csharp
> // Only for admin operations! Guard this carefully.
> var allStudentsAcrossAllTenants = await _db.Students
>     .IgnoreQueryFilters()  // Bypasses the TenantId filter
>     .CountAsync();
> ```
> Wrap this in a service that requires a special `Administrator` role so developers can't accidentally use it.

---

## 9. Row-Level Security in the Database

EF Core's global query filters are enforced at the application layer — they only work if all database access goes through EF Core. But what if:

- A developer writes raw SQL and forgets the TenantId filter?
- A different application connects directly to the database?
- A data migration script runs without going through EF Core?

For an extra layer of protection, you can enforce tenant isolation at the **database level** using **Row-Level Security (RLS)**. RLS is a feature in PostgreSQL and SQL Server that prevents certain rows from being returned, regardless of who writes the query.

```sql
-- ─────────────────────────────────────────────────────────────────
-- PostgreSQL Row-Level Security for LinguaLearn
-- ─────────────────────────────────────────────────────────────────

-- Step 1: Enable RLS on the Students table
ALTER TABLE "Students" ENABLE ROW LEVEL SECURITY;

-- Step 2: Create a policy that only allows access to rows
-- where the TenantId matches the current database session variable
CREATE POLICY tenant_isolation_policy ON "Students"
    USING ("TenantId" = current_setting('app.current_tenant_id'));

-- Step 3: Set the session variable before each query
-- (your application does this at the start of each request)
SET LOCAL app.current_tenant_id = 'tokyo-school-001';

-- Now ANY query on Students — even raw SQL — will only see Tokyo's rows:
SELECT * FROM "Students";
-- Result: Only Tokyo school's students, even though the SQL has no WHERE clause!
```

```csharp
// ─────────────────────────────────────────────────────────────────
// How to set the PostgreSQL session variable from C# / EF Core
// ─────────────────────────────────────────────────────────────────
public class TenantAwareDbContext : ApplicationDbContext
{
    public TenantAwareDbContext(
        DbContextOptions<ApplicationDbContext> options,
        ITenantContext tenantContext) : base(options, tenantContext)
    {
    }

    // Override OnConfiguring to set the session variable before any query
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await SetTenantSessionVariableAsync();
        return await base.SaveChangesAsync(cancellationToken);
    }

    // Call this before any raw SQL or query execution
    public async Task SetTenantSessionVariableAsync()
    {
        await Database.ExecuteSqlRawAsync(
            "SET LOCAL app.current_tenant_id = {0}", 
            _tenantContext.TenantId
        );
    }
}
```

With both EF Core query filters AND database-level RLS, you have **defense in depth**: two independent layers of tenant isolation. A bug in one layer doesn't compromise the whole system.

---

## 10. Tenant-Aware Feature Flags and Configuration

Different schools have different subscriptions, and different features should be available to different tenants. This is called **tenant-aware feature flagging**.

```csharp
// ─────────────────────────────────────────────────────────────────
// ITenantFeatureService.cs — Check what's enabled for this tenant
// ─────────────────────────────────────────────────────────────────
public interface ITenantFeatureService
{
    bool IsEnabled(string featureName);
    T GetSetting<T>(string settingKey, T defaultValue);
}

public class TenantFeatureService : ITenantFeatureService
{
    private readonly ITenantContext _tenantContext;
    private readonly IDistributedCache _cache;

    // Feature names as constants to avoid typos
    public static class Features
    {
        public const string LiveVideoClass = "live_video_class";
        public const string AiPronunciation = "ai_pronunciation";
        public const string AdvancedAnalytics = "advanced_analytics";
        public const string CustomLessonBuilder = "custom_lesson_builder";
        public const string ParentPortal = "parent_portal";
    }

    public bool IsEnabled(string featureName)
    {
        var tenantInfo = _tenantContext.TenantInfo;
        if (tenantInfo is null) return false;

        // Check the feature flags dictionary from TenantInfo
        return tenantInfo.Features.TryGetValue(featureName, out var isEnabled) && isEnabled;
    }

    public T GetSetting<T>(string settingKey, T defaultValue)
    {
        var tenantInfo = _tenantContext.TenantInfo;
        // Return tenant-specific setting, or platform default if not configured
        // ... implementation ...
        return defaultValue;
    }
}
```

```csharp
// ─────────────────────────────────────────────────────────────────
// LessonsController.cs — Using feature flags
// ─────────────────────────────────────────────────────────────────
[ApiController]
[Route("api/lessons")]
public class LessonsController : ControllerBase
{
    private readonly ILessonService _lessonService;
    private readonly ITenantFeatureService _features;

    public LessonsController(ILessonService lessonService, ITenantFeatureService features)
    {
        _lessonService = lessonService;
        _features = features;
    }

    [HttpPost("custom")]
    public async Task<IActionResult> CreateCustomLesson(CreateLessonRequest request)
    {
        // Only premium schools with the custom lesson builder can create lessons
        if (!_features.IsEnabled(TenantFeatureService.Features.CustomLessonBuilder))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                new { Message = "Custom lesson creation is not available in your plan. " +
                                "Please contact us to upgrade." });
        }

        var lesson = await _lessonService.CreateCustomLessonAsync(request);
        return CreatedAtAction(nameof(GetLesson), new { id = lesson.Id }, lesson);
    }
}
```

---

## 11. The Education Platform Scenario

Let's trace a complete request through the multi-tenant LinguaLearn system:

```
Scenario: A student at Tokyo English Academy opens their lesson dashboard.

1. HTTP Request arrives:
   GET https://tokyo.lingualearn.com/api/lessons
   Authorization: Bearer eyJhbGciOiJSUzI1NiJ9... (JWT)

2. TenantResolutionMiddleware runs:
   - Reads JWT claim: tenant_id = "tokyo-school-001"
   - Sets ITenantContext.TenantId = "tokyo-school-001"
   - Loads TenantInfo from Redis cache:
     { Name: "Tokyo English Academy", Plan: "premium", 
       Features: { ai_pronunciation: true, live_video: true } }

3. LessonsController.GetAllLessons() is called.
   Injects ILessonService (which injects ITenantContext and ApplicationDbContext)

4. ApplicationDbContext.Students.ToListAsync() executes:
   EF Core adds the Global Query Filter automatically.
   SQL Generated:
   SELECT * FROM "Lessons" 
   WHERE "TenantId" = 'tokyo-school-001'
   ORDER BY "Unit", "Level"
   
   Returns: 48 lessons belonging to Tokyo English Academy only.
   London School's 52 lessons: never touched. Never visible.

5. Response is assembled. TenantInfo.PrimaryColor = "#E74C3C" (Tokyo's red).
   The response includes this for the UI to render the school's branding.

6. Student sees their school's branded dashboard with their 48 lessons.
   A student at London School, in a separate request, sees London's 52 lessons
   in London's blue (#3498DB) branding.

Everything is isolated. No bugs required to maintain isolation.
It's enforced architecturally.
```

---

## 12. Decision Guide — Which Model Should You Choose?

| Criteria | Model 1 (Separate DB) | Model 2 (Separate Schema) | Model 3 (Shared Tables) |
|----------|-----------------------|---------------------------|-------------------------|
| Max practical tenants | ~100 | ~1,000 | Unlimited |
| Data isolation strength | Maximum | High | Good (with guards) |
| Infra cost | Highest | Medium | Lowest |
| Schema migration complexity | Very high | High | Low (once for all) |
| Data residency compliance | ✅ Easy | ⚠️ Possible | ❌ Hard |
| Per-tenant DB backup/restore | ✅ Easy | ✅ Easy | ❌ Hard |
| Implementation complexity | Medium | Medium | High |
| Performance isolation | ✅ Perfect | ✅ Good | ⚠️ Needs rate limiting |
| Best for | Regulated enterprise | Mid-market B2B | High-volume SaaS |

---

## 13. Summary and Key Takeaways

### Core Concepts

| Concept | One-Line Summary |
|---------|-----------------|
| Multi-tenancy | One application serving many isolated customers |
| Model 1: Separate DB | Maximum isolation, highest cost, best for regulated clients |
| Model 2: Separate Schema | Good isolation, moderate cost, practical for hundreds of tenants |
| Model 3: Shared Tables | Lowest cost, highest scalability, requires strict code discipline |
| TenantId | The discriminator column that identifies every row's owner |
| Tenant Resolution | How the app determines which tenant is making a request |
| Global Query Filter | EF Core's way of automatically scoping all queries by TenantId |
| Row-Level Security | Database-level enforcement of tenant isolation (defense in depth) |
| Feature Flags | Per-tenant toggles for features, enabling tiered subscription plans |

### The Golden Rules of Multi-Tenancy

1. **Resolve the tenant early** — The very first thing middleware should do is figure out which tenant is talking.
2. **Make isolation automatic** — Global Query Filters and RLS ensure developers can't forget tenant scoping.
3. **Index TenantId** — Every query filters by TenantId. Without an index, all queries become table scans.
4. **Cache TenantInfo** — TenantInfo is read on every request. Cache it in Redis to avoid DB hits.
5. **Test cross-tenant data leakage** — Write explicit tests that verify Tenant A cannot access Tenant B's data.

### What's Next

You can now build a multi-tenant application where hundreds of schools share the same infrastructure without seeing each other's data. In the next chapter, we tackle what happens when all those schools send so much traffic that your servers struggle to keep up.

*→ Continue to: [Chapter 3 — High Load Systems](./book_ch3_high_load_systems.md)*

---

*Chapter 2 Complete · 13 sections · Multi-Tenant Architecture*
