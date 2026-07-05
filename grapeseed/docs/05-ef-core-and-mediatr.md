# Chapter 5 — EF Core and MediatR

> *"Clean code always looks like it was written by someone who cares."*  
> — Robert C. Martin

---

## 5.1 MediatR: The Messenger Between Layers

MediatR is a simple .NET library that implements the **Mediator pattern**: rather than
components calling each other directly, they communicate through a central mediator.

In GrapeSeed, the mediator sits between the HTTP controllers and the business logic:

```
HTTP Request
    │
    ▼
Controller (thin adapter)
    │  sends a Command or Query object
    ▼
MediatR (mediator)
    │  finds the matching Handler
    ▼
CommandHandler / QueryHandler (business logic lives here)
    │  calls repositories, domain services, event publishers
    ▼
Response sent back through MediatR → Controller → HTTP Response
```

This means controllers contain almost no business logic. They parse the request, create a
command/query object, send it to MediatR, and return the result. The handler is where the
interesting work happens — and it can be unit tested independently of HTTP.

---

## 5.2 Commands and Queries: The CQRS Pattern

GrapeSeed uses **CQRS** (Command Query Responsibility Segregation) as implemented by MediatR:

- **Commands** change state. They have side effects. They return a `Result` (success/failure) but
  typically not data. Example: `RegisterTenantCommand`, `LoginCommand`, `UploadVideoCommand`.

- **Queries** read state. They have no side effects. They return data. Example:
  `GetTenantQuery`, `GetRecommendationsQuery`, `GetSignedUrlQuery`.

```csharp
// 📖 CONCEPT: Command — changes state, no returned data except success/failure
public sealed record RegisterTenantCommand(
    string Name,
    string Email,
    string PlanId,
    string StripePaymentMethodId
) : IRequest<Result<TenantId>>;

// 📖 CONCEPT: Query — reads state, returns data
public sealed record GetTenantQuery(TenantId TenantId) : IRequest<Result<TenantDto>>;
```

Using `record` types for commands and queries is a best practice: records are immutable by
default, they implement value equality (important for caching), and their `with` expression
makes it easy to create modified copies for testing.

---

## 5.3 MediatR Pipeline Behaviours

One of MediatR's most powerful features is **pipeline behaviours** — middleware that wraps
every command and query. In GrapeSeed, three behaviours run for every request:

```
Request enters
    │
    ▼
┌─────────────────────────────────┐
│     LoggingBehavior             │  ← Logs command name, start time
│  ┌──────────────────────────┐   │
│  │   ValidationBehavior     │   │  ← Runs FluentValidation rules
│  │  ┌───────────────────┐   │   │
│  │  │ TransactionBehavior│   │   │  ← Wraps commands in DB transaction
│  │  │  ┌─────────────┐  │   │   │
│  │  │  │   Handler   │  │   │   │  ← Your business logic
│  │  │  └─────────────┘  │   │   │
│  │  └───────────────────┘   │   │
│  └──────────────────────────┘   │
└─────────────────────────────────┘
    │
    ▼
Response exits
```

The beauty of this is that you write these cross-cutting concerns once and they apply to every
command without any extra code in the handlers. This is the Open/Closed Principle in action:
handlers are open to new behaviours but closed for modification.

```csharp
// 📖 CONCEPT: Pipeline Behaviour
// IPipelineBehavior<TRequest, TResponse> is the MediatR way to write middleware.
// 'next' is a delegate that calls the next behaviour in the pipeline (or the handler itself).
public class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var commandName = typeof(TRequest).Name;
        _logger.LogInformation("[START] {Command}", commandName);
        var stopwatch = Stopwatch.StartNew();

        var response = await next(); // ← calls next behaviour or handler

        stopwatch.Stop();
        _logger.LogInformation("[END] {Command} completed in {Ms}ms", commandName, stopwatch.ElapsedMilliseconds);
        return response;
    }
}
```

---

## 5.4 EF Core: Advanced Patterns

### Repository Pattern with Unit of Work

Even though EF Core's `DbContext` is itself a Unit of Work and repository, GrapeSeed wraps
it in explicit interfaces for two reasons:

1. **Testability**: Handlers can be tested against in-memory repositories without hitting a database.
2. **Abstraction**: If we ever swap EF Core for Dapper for performance-critical queries, only
   the repository implementations change — the handlers are untouched.

```csharp
// 📖 CONCEPT: Generic Repository
// The generic constraint <T> where T : Entity<TId> ensures that only proper
// domain entities (with an ID and domain events) can be stored through this interface.
public interface IRepository<T, TId> where T : Entity<TId>
{
    Task<T?> GetByIdAsync(TId id, CancellationToken ct = default);
    Task AddAsync(T entity, CancellationToken ct = default);
    void Update(T entity);
    void Delete(T entity);
}
```

### Value Objects and Owned Entities

Domain-Driven Design teaches us that not everything with a value should be an entity with an
identity. A `Money` value is defined only by its amount and currency — two `Money(100, "USD")`
objects are equal regardless of which object in memory they are.

```csharp
// 📖 CONCEPT: Value Object
// Inheriting from ValueObject ensures structural equality based on component values.
// Two Money instances with the same Amount and Currency are treated as identical.
public sealed class Money : ValueObject
{
    public decimal Amount { get; }
    public string Currency { get; }

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return Amount;
        yield return Currency;
    }
}
```

In EF Core, value objects are mapped as **Owned Entities** — they don't have their own table,
but are stored as columns in the owning entity's table:

```csharp
// In TenantConfiguration.cs:
// 💡 WHY: We don't want a separate 'MoneyAmounts' table.
// OwnsOne maps Money's columns directly into the 'Tenants' table.
builder.OwnsOne(t => t.SubscriptionFee, money =>
{
    money.Property(m => m.Amount).HasColumnName("subscription_fee_amount");
    money.Property(m => m.Currency).HasColumnName("subscription_fee_currency");
});
```

---

## 5.5 The Outbox Pattern: Guaranteed Event Delivery

Consider this scenario in the `RegisterTenantCommandHandler`:

```csharp
await _db.SaveChangesAsync();        // Step 1: Save tenant to database ✓
await _eventPublisher.PublishAsync(  // Step 2: Publish event to SNS
    new TenantRegisteredEvent(tenant.Id));
```

What happens if the application crashes between Step 1 and Step 2? The tenant exists in the
database, but the event was never published. VideoService and IdentityService never know about
the new tenant. The system is silently inconsistent.

The **Outbox Pattern** solves this:

```
Step 1: BEGIN TRANSACTION
Step 2: Save Tenant to 'tenants' table
Step 3: Save TenantRegisteredEvent to 'outbox_messages' table (same transaction)
Step 4: COMMIT TRANSACTION
              │
              │ (atomic — both succeed or both fail)
              ▼
Step 5: A background job reads 'outbox_messages' and publishes to SNS
Step 6: Mark outbox message as 'processed'
```

Because Steps 2 and 3 are in the same database transaction, they are atomic — either both
happen or neither happens. The background job in Step 5 can retry safely if SNS is temporarily
unavailable, because the message is durably stored in the database.

```csharp
// 📖 CONCEPT: Outbox message entity
// This is saved to the database in the same transaction as the business entity.
// It becomes the source of truth for "which events need to be published".
public sealed class OutboxMessage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string EventType { get; init; } = string.Empty;  // e.g., "TenantRegisteredEvent"
    public string Payload { get; init; } = string.Empty;    // JSON serialised event
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }              // null = not yet published
    public string? Error { get; set; }                      // set if publishing failed
}
```

---

## 5.6 EF Core Migrations in a Multi-Tenant System

With one schema per tenant, EF Core migrations require special handling. The normal
`dotnet ef database update` command only migrates one schema. GrapeSeed includes a
`MigrationRunner` utility that:

1. Reads all tenant IDs from the shared schema.
2. For each tenant, creates a DbContext pointed at that tenant's schema.
3. Applies any pending migrations to that schema.
4. Logs success or failure for each tenant individually.

```csharp
// 📖 CONCEPT: Programmatic migration
// Instead of the EF CLI tool, we call MigrateAsync() in code.
// This gives us control over which schema to migrate.
foreach (var tenant in tenants)
{
    using var tenantContext = CreateDbContextForTenant(tenant);
    await tenantContext.Database.MigrateAsync(); // applies pending migrations
    _logger.LogInformation("Migrated schema for tenant {TenantId}", tenant.Id);
}
```

---

*Continue to → [Chapter 6: Redis and PostgreSQL](./06-redis-and-postgres.md)*
