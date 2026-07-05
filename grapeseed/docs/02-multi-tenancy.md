# Chapter 2 — Multi-Tenancy

> *"One codebase. Many customers. Zero data leakage."*

---

## 2.1 What Is Multi-Tenancy?

Multi-tenancy is an architecture where a single application serves multiple customers
(called **tenants**) while keeping their data completely isolated from one another.

Think of it like an apartment building:
- One building (the application) serves many tenants (customers).
- Each tenant has their own locked apartment (their data).
- The landlord (your engineering team) maintains the building once, not separately for each tenant.

GrapeSeed serves multiple schools on a single platform. School A's students should never
see School B's videos, never receive School B's notifications, and never appear in School B's
reports — even though all their data lives in the same database server.

---

## 2.2 Three Approaches to Multi-Tenant Data Isolation

There are three common strategies, each representing a different point on the
isolation-vs-efficiency spectrum:

### Approach A: Separate Database per Tenant

```
Tenant A ──► Database A
Tenant B ──► Database B
Tenant C ──► Database C
```

**Pros:** Maximum isolation. A corrupted database for Tenant A cannot affect Tenant B.
**Cons:** Expensive. 1,000 tenants = 1,000 databases. Migrations become a coordination nightmare.
**When to use:** When tenants have strict regulatory requirements (e.g., HIPAA, GDPR data residency).

### Approach B: Shared Database, Separate Schema (GrapeSeed's Choice)

```
Database: grapeseed_main
├── Schema: tenant_schoolA
│   └── table: videos, students, watch_history
├── Schema: tenant_schoolB
│   └── table: videos, students, watch_history
└── Schema: shared
    └── table: tenants, plans
```

**Pros:** Strong isolation without per-database operational overhead. Easy to give tenants
their own backup schedules. Migrations can be applied schema-by-schema.
**Cons:** PostgreSQL has practical limits around the number of schemas (~hundreds is fine, thousands can be slow).
**When to use:** Most B2B SaaS products. This is the sweet spot.

### Approach C: Shared Database, Shared Schema with TenantId Column

```
table: videos
┌──────────┬──────────┬────────────────────┐
│ TenantId │ VideoId  │ Title              │
├──────────┼──────────┼────────────────────┤
│ school-a │ vid-001  │ Introduction to... │
│ school-b │ vid-002  │ Advanced Calculus  │
└──────────┴──────────┴────────────────────┘
```

**Pros:** Cheapest to operate. Simplest to migrate.
**Cons:** Every query must include `WHERE TenantId = ?`. A single missing WHERE clause is a data
breach. Requires strict code review practices and query-level enforcement (e.g., EF Core global query filters).
**When to use:** High-volume, low-sensitivity SaaS (e.g., a to-do list app).

---

## 2.3 How GrapeSeed Identifies the Current Tenant

Every HTTP request carries the tenant identity in the `X-Tenant-Id` header. The API Gateway
validates the JWT and injects this header before forwarding the request downstream.

```
Client ──► [API Gateway]
              │  Validates JWT
              │  Extracts tenant claim
              │  Injects X-Tenant-Id: school-a
              ▼
        [TenantService / VideoService / ...]
              │  Reads X-Tenant-Id from request
              │  Sets database search_path = tenant_schoola
              ▼
        [PostgreSQL]
              │  All queries automatically scoped to tenant_schoola schema
```

In ASP.NET Core, this is handled by a custom middleware called `TenantMiddleware`, which
reads the header and stores the resolved tenant in a scoped `ITenantContext` service:

```csharp
// 📖 CONCEPT: Middleware pipeline
// ASP.NET Core processes requests through a pipeline of middleware components.
// Each middleware can read/modify the request and response, then call the next middleware.
// We inject TenantMiddleware early in the pipeline so all downstream code can rely on
// ITenantContext being populated.
app.UseMiddleware<TenantMiddleware>();
```

See: `src/SharedKernel/Infrastructure/MultiTenancy/TenantMiddleware.cs`

---

## 2.4 PostgreSQL Schema Switching with EF Core

The magic of the per-schema approach lies in PostgreSQL's `search_path` setting. When you
set `search_path = tenant_schoola`, every unqualified table reference (`SELECT * FROM videos`)
automatically resolves to `tenant_schoola.videos`.

In EF Core, we override `OnConfiguring` to set the search path on every new connection:

```csharp
// 📖 CONCEPT: DbContext per-tenant schema switching
// We override the connection open event to set PostgreSQL's search_path.
// This means all EF Core queries for this request are automatically scoped
// to the correct tenant schema — no WHERE TenantId = ? needed on every query.
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    // The schema name is injected from ITenantContext
    modelBuilder.HasDefaultSchema(_tenantContext.SchemaName);
}
```

### EF Core Global Query Filters (Defence in Depth)

Even with schema isolation, we add a global query filter as a second line of defence.
This is especially important for the shared schema (used by TenantService itself):

```csharp
// 📖 CONCEPT: Global Query Filters
// Applied automatically to every LINQ query on this entity type.
// You never need to remember to add .Where(x => x.TenantId == currentTenantId)
// because EF Core adds it for you. Forgetting to do so would be a data breach.
modelBuilder.Entity<Tenant>().HasQueryFilter(t => t.Id == _tenantContext.TenantId);
```

---

## 2.5 Tenant Provisioning Flow

When a new school registers, the following sequence runs:

```
1. POST /api/tenants/register
        │
        ▼
2. [TenantService] Validates input → creates Tenant record in shared schema
        │
        ▼
3. [StripePaymentService] Charges the tenant's card → receives confirmation
        │
        ▼
4. [TenantService] Creates PostgreSQL schema:  tenant_{slug}
   Creates tables:  videos, students, watch_history (via EF Core migration)
        │
        ▼
5. [SNS] Publishes TenantRegisteredEvent
        │
        ├──► [SQS → VideoService] Sets up default video library
        └──► [SQS → IdentityService] Prepares identity tables for new tenant
```

This entire flow is orchestrated by the `RegisterTenantCommandHandler`. Notice how each step
is either *committed to the database* or *published to SNS* — never both in the same transaction.
This is the **Outbox Pattern**, explained in detail in Chapter 5.

---

## 2.6 Common Pitfalls

### Pitfall 1: Forgetting the Schema in Background Jobs

Background jobs (e.g., sending weekly email digests) run outside of an HTTP request. There is
no `X-Tenant-Id` header to read. You must explicitly pass the tenant ID to the job and set up
the `ITenantContext` manually at the start of job execution.

### Pitfall 2: Cross-Tenant Joins

With the per-schema approach, you cannot easily join data across tenants. If you need analytics
across all tenants (e.g., "how many total videos were watched today?"), you need a separate
reporting pipeline that aggregates data from all schemas into a central analytics schema.

### Pitfall 3: Migrations at Scale

Applying an EF Core migration to 500 tenant schemas must be done carefully:
- Run migrations in parallel (but limit concurrency to avoid overwhelming the database).
- Log each schema result. If one fails, continue with others and alert.
- Test migrations on a staging copy of a large tenant's schema first.

---

*Continue to → [Chapter 3: Microservices](./03-microservices.md)*
