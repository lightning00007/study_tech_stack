# Chapter 11: EF Core, MediatR, and PostgreSQL — The Full Picture

> *"The joy of software architecture is in the composition: individually, each piece is modest. Together, they form something that truly sings."*

---

## 11.1 Why This Chapter Exists

You have already met MediatR in Chapter 9, where you learned to think in commands and queries. You met PostgreSQL in Chapter 1, where you understood MVCC, indexes, and the engine behind the data. And you have seen Unit of Work and Repository in Chapter 10.

But there is a gap. None of those chapters answered the deep, practical question that surfaces when you sit down at your keyboard and begin building a real feature:

**How do all three of these things actually work together, in concert, as a living system?**

This chapter bridges that gap. It is not about three isolated topics — it is about the relationship between them. Think of it like learning to cook: you already know what a knife is, and you know what an onion is. This chapter teaches you the cut.

---

## 11.2 The Mental Model: Three Layers, One Flow

Before we touch any concept in detail, you need to internalize the central flow of a request in a system that uses all three tools together. Every request your application handles follows the same journey:

```
HTTP Request
     |
     v
[Controller / Minimal API Endpoint]
     |  Creates a Command or Query object
     v
[MediatR -- mediator.Send()]
     |  Routes to the correct Handler
     v
[Handler -- your business logic]
     |  Uses EF Core (via IRepository or directly via DbContext)
     v
[EF Core -- your translation layer]
     |  Translates C# LINQ into SQL
     v
[PostgreSQL -- the source of truth]
     |  Executes the query, returns rows
     v
(back up the chain, mapped to response DTOs)
```

Each layer has a contract with the next:
- **MediatR** does not know what EF Core is. It only knows about the Handler.
- **EF Core** does not know what MediatR is. It only knows about DbContext and entities.
- **PostgreSQL** does not know what C# is. It only knows SQL.

This separation is not accidental — it is deliberate design. Each layer can be tested, replaced, or optimized independently of the others. That is the promise of the architecture, and the rest of this chapter shows you how to make that promise real.

---

## 11.3 EF Core — What It Actually Is

Many developers treat Entity Framework Core as a "SQL generator." That is true but deeply incomplete. EF Core is three things simultaneously:

**1. An Object-Relational Mapper (ORM)**
It maps your C# classes (entities) to database tables, and your C# properties to columns. When you write `order.Status = OrderStatus.Shipped`, EF Core remembers that change and knows which SQL UPDATE statement to generate.

**2. A Unit of Work**
Every DbContext instance is itself a Unit of Work. It tracks every object it hands you, records every change you make to those objects, and commits everything as one atomic transaction when you call `SaveChangesAsync()`. Nothing is written to the database before that moment.

**3. An Identity Map**
Within a single request, if you load the same entity twice (by the same primary key), EF Core returns the same C# object, not two copies. This prevents the bizarre situation where you update one copy of an order and then load a second copy that still shows the old data — both copies are the same reference in memory.

Understanding these three roles is the difference between using EF Core as a clever shortcut and truly using it as a design tool.

---

## 11.4 The Change Tracker — EF Core's Inner Eye

The heart of EF Core is the **Change Tracker**. Every entity that comes from a query using your DbContext is in a tracked state. The Change Tracker watches it like a hawk.

There are four states an entity can be in:

| State | Meaning |
|---|---|
| **Added** | This entity is new. EF Core will INSERT it. |
| **Unchanged** | Loaded from DB. Nothing has changed. EF Core will not touch it. |
| **Modified** | At least one property changed. EF Core will UPDATE only the changed columns. |
| **Deleted** | You called Remove(). EF Core will DELETE it. |

When you call `SaveChangesAsync()`, EF Core walks through every tracked entity, looks at its state, and generates the appropriate SQL. This is why EF Core "just works" when you load an entity, change a property, and save — there is no separate "mark as dirty" call required.

### 11.4.1 The Cost of Tracking

Tracking is not free. EF Core must create an internal snapshot of every entity it loads (to know what changed). For a query that returns thousands of rows, this overhead can be significant — you are allocating memory for the original snapshot of every row.

This leads to one of the most important rules in EF Core:

> **Use AsNoTracking() on every read-only query.**

When you call `.AsNoTracking()`, EF Core skips the snapshot and the Change Tracker entirely. The entity is handed to you as a plain C# object, with no strings attached. Reads become faster and use less memory. Since Queries (in CQRS) never modify data, they should always use `AsNoTracking()`.

Commands, on the other hand, load entities *with* tracking, because they need EF Core to detect and persist the changes they make.

---

## 11.5 Designing Your DbContext for a Real Application

Your DbContext is the central configuration point for EF Core. It deserves thoughtful design, not just a handful of `DbSet<T>` properties.

### 11.5.1 Naming and Lifetime

Your DbContext class represents your **database boundary** — the scope of data your application directly owns. In a small system, you may have one. In a larger system with microservices or bounded contexts, you may have one per service (or even one per bounded context within a monolith).

The DbContext lifetime should be **scoped** — one instance per HTTP request. This is the default when you register it with `AddDbContext<>()`. A scoped lifetime means all the handlers, repositories, and services within a single request share the same DbContext instance, which is essential for the Unit of Work pattern to work correctly.

Never use a singleton DbContext. It is not thread-safe, and it would mean one Change Tracker accumulating changes from thousands of concurrent requests — a recipe for data corruption.

### 11.5.2 Configuring Entities with Fluent API

EF Core gives you two ways to configure your entities: **Data Annotations** (attributes on properties) and the **Fluent API** (configuration in `OnModelCreating`). In a professional codebase, always prefer the Fluent API.

Here is why: Data Annotations pollute your domain model with infrastructure concerns. Your `Order` class represents a business concept. It should not know that its `Status` column has a maximum length of 50 characters in the database. That is a persistence concern, not a domain concern. The Fluent API keeps those two worlds separate.

A common pattern is to create **separate configuration classes** that implement `IEntityTypeConfiguration<T>`, one per entity. This keeps `OnModelCreating` clean and your configurations organized and independently readable.

### 11.5.3 Global Query Filters — Soft Deletes Made Easy

PostgreSQL records are never truly gone when you soft-delete them — you set an `IsDeleted = true` flag and let them linger. The problem is that every query must then remember to add `WHERE is_deleted = false`. Forget once, and you surface deleted data.

EF Core's **Global Query Filters** solve this elegantly. You configure a filter on the entity type, and EF Core automatically appends it to every LINQ query for that type. Your handlers never need to remember it — the filter is always there, invisibly.

You can temporarily bypass it with `.IgnoreQueryFilters()` when you genuinely need to query deleted records (for example, in an admin restore feature).

---

## 11.6 PostgreSQL Through the Eyes of EF Core

EF Core is database-agnostic in principle, but using it with PostgreSQL through **Npgsql.EntityFrameworkCore.PostgreSQL** (the Npgsql provider) unlocks capabilities that simply do not exist in the generic EF Core.

### 11.6.1 JSONB Columns

PostgreSQL's JSONB column type stores JSON data in a compressed binary format, with full index support. EF Core with the Npgsql provider lets you map a C# class directly to a JSONB column. You write LINQ, the provider writes JSONB-aware SQL.

This is transformative for semi-structured data. Imagine you have an `Order` entity with a `ShippingAddress` that varies slightly between domestic and international orders. Rather than creating three normalized tables, you can store the address as JSONB. EF Core will serialize your C# object to JSON when saving and deserialize it back when loading — all transparently.

You can even filter on fields *inside* the JSONB column using LINQ. The Npgsql provider translates this into PostgreSQL's `->` and `->>` operators. You get type-safe LINQ in C# that becomes native PostgreSQL JSONB queries.

### 11.6.2 Arrays

PostgreSQL has a native array type. Rather than creating a separate junction table for simple string arrays, you can store them directly in a column. The Npgsql provider maps a `List<string>` or `string[]` property to a PostgreSQL array column. You can filter on elements within the array using LINQ's `.Any()` or `.Contains()`.

### 11.6.3 Enums

By default, EF Core stores enums as integers. This is opaque and brittle — your PostgreSQL records contain 0, 1, 2, with no indication of what those mean. The Npgsql provider lets you create **native PostgreSQL enum types**. Your database column stores the string value (Pending, Shipped, Delivered), making rows human-readable and eliminating the need to cross-reference your C# code to understand your data.

### 11.6.4 Full-Text Search

PostgreSQL has powerful built-in full-text search. The Npgsql provider exposes this through EF Core with methods like `EF.Functions.ToTsVector()` and `EF.Functions.ToTsQuery()`. You can do weighted, dictionary-aware, ranked full-text search entirely through LINQ, without dropping down to raw SQL.

### 11.6.5 Sequences and GENERATED Columns

PostgreSQL's `GENERATED ALWAYS AS` computed columns let you store a value that the database automatically calculates from other columns. EF Core maps these as `ValueGeneratedAlways` computed properties — you read them freely in C#, but you never set them; the database owns their value.

---

## 11.7 Migrations — The Evolution of Your Schema

A migration is EF Core's way of describing a change to your database schema. It is a snapshot of the difference between the current state of your DbContext model and the last known state of your database.

### 11.7.1 The Mental Model of Migrations

Think of your database schema as a piece of clay. When you first create it, you shape it into its initial form — that is your initial migration. As your application evolves, you need to reshape the clay: add a column here, rename a table there. Each reshaping step is a migration.

EF Core maintains a table in your database called `__EFMigrationsHistory`. Every migration that has been applied is recorded there. When you run `dotnet ef database update`, EF Core looks at this table, compares it to the migration files in your project, and applies only the ones that have not yet run. This makes database updates idempotent and reproducible across environments.

### 11.7.2 The Development Flow

The flow is always the same:

1. You change a C# entity class or configuration
2. You run `dotnet ef migrations add DescriptiveName`
3. EF Core generates a migration file with `Up()` (apply the change) and `Down()` (reverse the change)
4. You review the generated file — this is a critical step many developers skip
5. You run `dotnet ef database update` to apply it locally
6. You commit both the entity change and the migration file to source control

The migration file is code. It lives in your repository alongside your C# source. It should be reviewed in pull requests. A bad migration — one that locks a table for minutes on a heavily-loaded PostgreSQL database — is a production incident waiting to happen.

### 11.7.3 Migrations and PostgreSQL: What to Watch For

PostgreSQL handles many schema changes gracefully and without locking. Adding a nullable column, adding an index with CONCURRENTLY, adding a new table — all of these are fast, non-locking operations.

But some operations are dangerous on live databases:

- **Adding a NOT NULL column without a default** locks the entire table while PostgreSQL rewrites it. The safe approach is to add it as nullable first, backfill the data, then add the NOT NULL constraint.
- **Creating a regular index** (without CONCURRENTLY) acquires a lock that blocks writes. EF Core generates regular index creation by default. For large tables on production systems, you may need to apply the index migration manually using `CREATE INDEX CONCURRENTLY`, then let EF Core's `__EFMigrationsHistory` catch up.
- **Renaming a column** requires coordinated deployment because the old name disappears instantly. A blue-green deployment or a multi-step migration (add new column, copy data, rename, remove old column) is the safe path.

Understanding these nuances is what separates a developer who uses EF Core from one who uses EF Core *well*.

---

## 11.8 MediatR + EF Core: The Handler as the Unit of Work

In Chapter 10, you learned about the Unit of Work pattern abstractly. Now let us make it concrete.

In a MediatR-based system, each **Handler** is the natural boundary of a Unit of Work. A command handler:

1. Starts (implicitly, via the scoped DbContext)
2. Loads one or more entities — possibly from multiple repositories
3. Applies business logic — calls methods on those entities
4. Saves all changes in a single `SaveChangesAsync()` call — the commit point

This is the Unit of Work pattern in its simplest and most natural form. The DbContext is the coordinator. EF Core tracks changes across all the entities it loaded. `SaveChangesAsync()` issues all the INSERT, UPDATE, and DELETE statements in one database transaction.

You do not need a separate `IUnitOfWork` interface if your handlers talk to a single DbContext. The DbContext is already a unit of work. The separate interface becomes valuable when you need to test handlers without a database (mock the interface), or when you want to make the commit point explicit and visible in your handler code rather than implicit in the DbContext lifetime.

### 11.8.1 The Handler's Perspective

Imagine a `ProcessOrderPaymentCommandHandler`. Its job is to:
- Load the order
- Load the customer's payment method
- Record a payment transaction
- Update the order's status to Paid
- Schedule a domain event to be published after the commit

All of this happens within a single handler method. The handler loads entities, changes them, adds new ones, and then calls `SaveChangesAsync()`. PostgreSQL processes the entire batch as one atomic transaction. Either everything succeeds, or nothing does. The order never ends up in a state where the payment is recorded but the status was not updated.

This atomic guarantee is one of the deepest reasons to prefer a single-database design: it eliminates entire categories of distributed consistency problems.

---

## 11.9 Pipeline Behaviors and Database Concerns

Chapter 9 introduced Pipeline Behaviors as middleware for MediatR. When you combine them with EF Core and PostgreSQL, they become even more powerful.

### 11.9.1 The Transaction Behavior

By default, `SaveChangesAsync()` wraps its changes in an implicit transaction. But sometimes a command does work in multiple places — it calls `SaveChangesAsync()` midway through to persist some data, then does more work and saves again. If the second save fails, the first one already committed.

A **Transaction Behavior** wraps the entire handler in an explicit database transaction. The transaction begins before the handler runs. If the handler completes without throwing, the behavior commits. If any exception is thrown, the behavior rolls back the entire operation. Not a single byte makes it to the database unless every step succeeds.

This is registered as a pipeline behavior that applies to all commands (but not queries — transactions are not needed for read-only operations). It is a cross-cutting concern: you write it once, register it once, and every command automatically benefits from it.

### 11.9.2 The Optimistic Concurrency Behavior

PostgreSQL has no inherent concept of "someone else changed this record while you were looking at it." If two users load the same order simultaneously and both try to update the status, the last write wins — silently.

EF Core's **concurrency tokens** fix this. You add a concurrency token to your entity (for PostgreSQL, the `xmin` system column is perfect — it changes every time the row is updated at the PostgreSQL level). EF Core uses this in its UPDATE statements:

```
UPDATE orders
SET status = 'Shipped'
WHERE id = 42 AND xmin = 1234567
```

If `xmin` has changed since you loaded the entity (because someone else updated the row), the WHERE clause matches zero rows. EF Core throws a `DbUpdateConcurrencyException`.

A **Concurrency Behavior** in your MediatR pipeline can catch this exception and either retry the command automatically (appropriate for low-conflict scenarios) or return a meaningful conflict response to the client (appropriate when you want the user to refresh and decide).

### 11.9.3 The Audit Behavior

Every entity in a professional system should have `CreatedAt`, `CreatedBy`, `UpdatedAt`, and `UpdatedBy` fields. Filling these in manually in every handler is tedious and easy to forget.

The clean solution is to override `SaveChangesAsync()` in your DbContext. Before delegating to the base implementation, you iterate over the Change Tracker's entries. For every entity in Added state, you set `CreatedAt = now` and `CreatedBy = currentUserId`. For every entity in Modified state, you set `UpdatedAt = now` and `UpdatedBy = currentUserId`.

The current user ID comes from injecting `ICurrentUserService` (a small interface that wraps `IHttpContextAccessor`) into your DbContext. The handler never thinks about audit fields — they are handled automatically at the persistence layer.

---

## 11.10 Queries: The Art of Projections

Queries in a CQRS system have one job: return data as fast as possible in the exact shape the client needs. EF Core's LINQ provider is your translation tool, but knowing *how* to write efficient queries is a skill in itself.

### 11.10.1 Always Project to DTOs in Queries

The single most important performance technique for queries is using **Select projections** to avoid loading unnecessary data.

When you call `DbContext.Orders.Find(id)`, EF Core loads the entire order row — every column, including columns you do not need for this particular response. If your query handler is returning a summary (just the order ID, status, and total), you are loading five times more data than necessary.

Instead, write your query to project directly to the DTO:

```
Select the id, status, and total from Orders
where id matches the requested id
```

EF Core translates this into SQL that only fetches the three columns you need. The result is never a tracked entity — it is a plain DTO object. Smaller data transfer, no Change Tracker overhead, and the query runs faster.

### 11.10.2 Include vs. Projection for Related Data

A common mistake is using `.Include()` (EF Core's eager loading) in queries to load related entities, then mapping them to a DTO. Include causes a SQL JOIN and loads full entity rows from both tables. If you then only need two fields from the related table, you have loaded the rest for nothing.

The superior pattern for queries is to do the projection in a single `Select()` that spans both tables. EF Core generates an efficient JOIN and fetches only the columns you reference in the projection. The same result, but potentially dozens of wasted columns are never transferred from PostgreSQL.

Use `.Include()` in **commands** (where you load an entity to apply business logic and all its related data matters). Use **projection** in **queries** (where you read data for display and know exactly what fields you need).

### 11.10.3 Pagination

PostgreSQL is excellent at pagination with LIMIT and OFFSET. EF Core translates `.Skip()` and `.Take()` to exactly these SQL clauses.

However, OFFSET pagination has a subtle performance problem that only reveals itself at scale: to skip 10,000 rows, PostgreSQL must read through 10,000 rows and discard them. At large offsets (page 500 of a result), the query slows down.

The alternative is **keyset pagination** (also called cursor-based pagination): instead of "skip 10,000 rows," you say "give me rows where id > lastSeenId." This query uses an index and is fast regardless of how deep in the result set you are. The trade-off is that the client cannot jump to an arbitrary page number — it can only go forward and backward from the current position.

For most application UIs (infinite scroll, "load more" patterns), keyset pagination is strictly better. For UIs with page number selectors, OFFSET is simpler and acceptable if the dataset is not enormous.

---

## 11.11 Domain Events: The Bridge Between MediatR and Your Database

One of the most elegant patterns that emerges from using MediatR with EF Core is the **Domain Events** pattern. It solves a real problem: what happens after a business operation completes?

When you process a payment, you want to:
- Send a confirmation email
- Update the customer's loyalty points
- Notify the shipping department

These are side effects. They should not be in the payment handler — that handler's job is to process the payment, not to know about emails, loyalty programs, or shipping.

### 11.11.1 Raising Events on Entities

The pattern works like this: your domain entity (the `Order` class) raises a **domain event** when something significant happens to it.

```
order.MarkAsPaid(paymentId)
// internally: AddDomainEvent(new OrderPaidEvent(this.Id, paymentId))
```

The event is not dispatched immediately. It is stored in a collection on the entity. This is important: the event exists only in memory, attached to the entity object.

### 11.11.2 Dispatching Events After Saving

The second part of the pattern happens in your DbContext's `SaveChangesAsync()` override. After the base `SaveChangesAsync()` succeeds (the database transaction committed), you collect all domain events from all tracked entities and dispatch them through MediatR as `INotification` objects.

```
1. Handler calls SaveChangesAsync()
2. DbContext: save to PostgreSQL -> COMMIT
3. DbContext: collect all domain events from tracked entities
4. DbContext: mediator.Publish(OrderPaidEvent)
5. OrderPaidEventHandler: send confirmation email
6. LoyaltyPointsHandler: update loyalty points (also a handler for the same event)
```

Each event handler runs independently. If one fails, it does not roll back the payment — the payment already committed. This is **eventual consistency within a single process**: the core operation is guaranteed, and the side effects follow shortly after.

For truly critical side effects (ones that must be guaranteed even if the application crashes between steps 2 and 5), you need the Outbox pattern — publishing events to a database table in the same transaction, then dispatching them from a background process. That is the next level of sophistication, and it builds directly on the foundation you are building here.

---

## 11.12 Testing the Integration: What to Test Where

The beauty of the three-layer architecture is that each layer can be tested in isolation.

### 11.12.1 Testing Handlers in Isolation

A handler that uses EF Core can be tested with the **EF Core InMemory provider** or, better yet, with **SQLite in-memory mode**. You create a real DbContext backed by an in-memory database, seed it with test data, run your handler, and assert the outcome.

These tests are fast (no network, no external database) and test real logic including Change Tracker behavior, domain event dispatching, and validation. They are not true unit tests (they involve a real database engine), but they are fast enough to run in CI on every commit.

For handlers that have external side effects (sending emails, publishing to SQS), inject those dependencies as interfaces and mock them. Your handler tests verify business logic, not infrastructure.

### 11.12.2 Testing Queries Against Real PostgreSQL

Integration tests that run against a real PostgreSQL database (in Docker, seeded fresh for each test run) are more expensive but catch things that in-memory tests cannot: index usage, query performance, JSONB operator behavior, `AsNoTracking()` edge cases.

Use **Testcontainers for .NET** to spin up a disposable PostgreSQL container per test class or per test run. The container starts, migrations run, tests execute, and the container is destroyed. This pattern gives you high confidence that your queries work correctly against the actual database engine your production system uses.

### 11.12.3 What Not to Test

Do not write tests that test EF Core itself — Microsoft does that. Do not write tests that verify that LINQ translates to SQL correctly — that is Npgsql's responsibility. Test your *business logic*, your *query results given known data*, and your *error handling* when the database behaves unexpectedly.

---

## 11.13 Performance: The Questions You Should Always Ask

### 11.13.1 The N+1 Problem

This is the most common EF Core performance mistake, and it is insidious because it is invisible in code — it only reveals itself in the SQL logs.

The N+1 problem occurs when you load a list of N entities and then, for each entity, trigger a separate database query to load a related entity. You intended one query; you got N+1.

The solution is to think about your data access pattern before writing the query. If you know you will need the related data, load it in the initial query — either with `.Include()` or by projecting to a DTO that includes the related fields in a single `Select`.

**Always run your queries with SQL logging enabled during development.** EF Core logs every SQL statement it sends to PostgreSQL. Seeing ten queries where you expected one is an immediate sign of an N+1 problem. Catching it in development takes five minutes. Catching it in production (as a performance crisis) takes hours.

### 11.13.2 Compiled Queries

Every time EF Core translates a LINQ expression to SQL, it goes through a compilation step — parsing the expression tree and building the SQL string. For queries that run thousands of times per second, this compilation cost adds up.

**Compiled queries** let you perform this compilation once, at application startup, and cache the result. Subsequent executions skip the compilation entirely. The query is passed its parameters, and the cached SQL template is used directly. For hot paths (the most frequently executed queries in your application), compiled queries can reduce query execution time measurably.

### 11.13.3 Bulk Operations

EF Core's default behavior is to issue one SQL statement per entity changed. Inserting 1,000 orders means 1,000 INSERT statements. This is fine for typical CRUD operations but catastrophic for data imports, batch jobs, or seeding.

EF Core 7 introduced **ExecuteUpdateAsync** and **ExecuteDeleteAsync** — methods that translate directly to bulk UPDATE and DELETE statements without loading entities into memory and tracking them. For bulk writes, this is dramatically more efficient.

For even more extreme cases (millions of rows), the **Npgsql COPY API** lets you stream data directly into PostgreSQL using the native binary COPY protocol — the fastest possible data loading mechanism, bypassing SQL parsing entirely.

---

## 11.14 The Complete Picture: A Feature, End to End

Let us trace a single feature through the entire stack to make everything concrete.

**Feature: A customer updates their shipping address.**

### The Journey

**Step 1 — The HTTP request arrives.**
The controller receives a `PUT /customers/{id}/address` request with a JSON body. It creates an `UpdateShippingAddressCommand` object and calls `mediator.Send(command)`.

**Step 2 — MediatR pipeline activates.**
Before reaching the handler, the command passes through:
- **ValidationBehavior**: FluentValidation checks that the address fields are not empty, that the postal code format is valid for the given country, and that the country code is on the allowed list.
- **TransactionBehavior**: Opens an explicit database transaction.
- **LoggingBehavior**: Logs the command name and its parameters (excluding sensitive data).

**Step 3 — The handler runs.**
`UpdateShippingAddressCommandHandler` injects `ICustomerRepository` (which wraps the scoped DbContext). It calls `repository.GetByIdAsync(command.CustomerId)`. EF Core generates:

```sql
SELECT c.id, c.name, c.email, c.shipping_address
FROM customers c
WHERE c.id = $1 AND c.is_deleted = false
```

The `is_deleted = false` clause is added automatically by the Global Query Filter — the handler never wrote it.

The handler loads the entity (which is now tracked by the Change Tracker). It calls `customer.UpdateShippingAddress(command.NewAddress)` — a method on the entity that validates the address transition is allowed by business rules (perhaps a VIP customer cannot change to a PO box address).

`UpdateShippingAddress()` internally calls `AddDomainEvent(new ShippingAddressChangedEvent(this.Id, oldAddress, newAddress))`.

**Step 4 — The unit of work commits.**
The handler calls `unitOfWork.SaveChangesAsync()`. In the DbContext.SaveChangesAsync() override:
- The audit middleware sets `customer.UpdatedAt = now` and `customer.UpdatedBy = currentUserId` automatically.
- The base `SaveChangesAsync()` runs. EF Core sees that `customer` is in Modified state. It generates:

```sql
UPDATE customers
SET shipping_address = $1, updated_at = $2, updated_by = $3
WHERE id = $4 AND xmin = $5
```

The `xmin = $5` is the concurrency token check. If another request modified this customer concurrently, this UPDATE will match zero rows and EF Core will throw `DbUpdateConcurrencyException`, which the Transaction Behavior catches and translates to a 409 Conflict response.

- After the base save completes, the override collects the `ShippingAddressChangedEvent` and calls `mediator.Publish(event)`.
- The TransactionBehavior commits the transaction.

**Step 5 — Domain events dispatch.**
`ShippingAddressChangedEventHandler` runs. It queues a job to send a confirmation email (asynchronously, via SQS). It does not send the email synchronously — it only queues the intent. If the email service is down, the message waits in SQS and will be processed when it recovers.

**Step 6 — Response.**
The handler returns `Result.Success()`. MediatR routes this back to the controller, which responds with `204 No Content`.

---

From a single HTTP request, through MediatR's pipeline, into an EF Core change-tracked entity, through PostgreSQL's optimistic concurrency check, and out through a domain event to a downstream service — this is what the three tools look like in motion, together, as a unified system.

---

## 11.15 Key Takeaways

| Concept | The Thing to Remember |
|---|---|
| DbContext lifetime | Scoped — one per request. Never singleton. |
| Change Tracker | Watches every loaded entity. SaveChangesAsync() is the commit point. |
| AsNoTracking() | Always in Query handlers. Never for Command handlers that modify data. |
| Fluent API | Keep domain models clean. Use separate IEntityTypeConfiguration classes. |
| Global Query Filters | Your soft-delete filter goes here. Written once, applied everywhere. |
| Migrations | Review the generated file. Know which operations lock tables in PostgreSQL. |
| Pipeline Behaviors | Transaction, concurrency, audit — cross-cutting concerns extracted from handlers. |
| Domain Events | Raised on entities, dispatched after SaveChangesAsync() succeeds. |
| N+1 Problem | Enable SQL logging in development. Catch it there, not in production. |
| Select Projections | The single biggest query performance improvement in a CQRS system. |
| Compiled Queries | Cache the LINQ-to-SQL translation for hot-path queries. |
| Bulk Operations | ExecuteUpdateAsync / ExecuteDeleteAsync for batch operations without entity tracking. |

---

## 11.16 What Comes Next

You now have the complete picture of how EF Core, MediatR, and PostgreSQL interact as a system. The natural next steps in your learning are:

- **The Outbox Pattern**: Guaranteeing that domain events are dispatched even if the process crashes between the database commit and the MediatR publish.
- **Read Models / Projections**: Maintaining a separate, denormalized read database (optimized for queries) that is updated by domain event handlers — the full CQRS read/write split.
- **Dapper as a Complement**: Using raw SQL via Dapper for complex analytical queries where EF Core's translation is not optimal, while keeping EF Core for all writes.
- **Connection Resilience**: Configuring `EnableRetryOnFailure()` and understanding when PostgreSQL transient errors occur and how to handle them gracefully.

Each of these topics builds on exactly the foundation you have constructed in this chapter. The architecture scales from a single-developer side project to a team of fifty building a high-traffic service — and the mental models remain the same throughout.
