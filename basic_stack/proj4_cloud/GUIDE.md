# 📖 Project 4 — Cloud-Native Architecture
### *"Guarantee event delivery. Scale. Observe."*

> *"A distributed system is one in which the failure of a computer you didn't even know existed can render your own computer unusable."*  
> — Leslie Lamport

---

## What Changed from Project 3?

Project 3 gave us a clean, well-structured CQRS application. But it has a critical blind spot: **it assumes the world is reliable**. When `CreateBookCommand` succeeds, the book is in the database. But what about downstream systems — the email notification service, the search indexer, the analytics pipeline?

If we tried to notify them directly inside the handler and any one fails, we have two choices:
1. Return success to the caller (book is saved, but notifications silently dropped)
2. Return failure to the caller (roll back the book save — the book was never created)

Neither is acceptable. Project 4 introduces the patterns that solve this: **domain events**, the **Outbox Pattern**, and **AWS SNS/SQS** for reliable async messaging.

---

## Chapter 1 — Domain Events and Aggregate Roots

### What Is a Domain Event?

A domain event is a record of something significant that happened within the domain. It is a **historical fact** — it cannot be undone, only compensated for.

Domain events are:
- Named in **past tense**: `BookCreated`, `BookPublished`, `AuthorDeleted`
- **Immutable**: a record (or a class with init-only properties)
- **Rich with context**: they carry enough data for consumers to act without querying back

```csharp
// A domain event carries the facts of what happened
public sealed record BookPublishedEvent(
    Guid EventId,        // For deduplication
    DateTime OccurredAt, // When it happened
    int BookId,          // What was affected
    string Title,        // What it was called
    string AuthorName    // Who wrote it
) : IDomainEvent;
```

### What Is an Aggregate Root?

An aggregate root is a cluster of related domain objects treated as a single unit. The aggregate root is the **only entry point** — external code cannot modify inner objects directly.

```
 ┌─────────────────────────────────────────────┐
 │              Book (AggregateRoot)           │
 │                                             │
 │   Title, ISBN, IsPublished, ...             │
 │                                             │
 │   + Book.Chapters[]                         │
 │   + Book.Reviews[]                          │
 │                                             │
 │   External code can only call:              │
 │     book.Publish()                          │
 │     book.AddChapter(...)                    │
 │   NOT:                                      │
 │     book.Chapters.Add(...)  ← FORBIDDEN     │
 └─────────────────────────────────────────────┘
```

The aggregate root enforces **invariants** — business rules that must always be true. "A published book cannot be archived without going through review" is an invariant. Only `Book.Archive()` can verify and enforce this.

### Why Raise Domain Events Inside the Entity?

When `Book.Publish()` is called, it does two things:

```csharp
public void Publish()
{
    if (IsPublished)
        throw new InvalidOperationException("Already published.");

    IsPublished = true;

    // The event is RAISED INTERNALLY — not published externally
    RaiseDomainEvent(new BookPublishedEvent(
        EventId: Guid.NewGuid(),
        OccurredAt: DateTime.UtcNow,
        BookId: Id,
        Title: Title,
        AuthorName: Author.FullName
    ));
}
```

Why not call SNS directly here? **Because `Book` is a domain class.** It must not import AWS SDK. It must not know about message brokers. It must remain a pure business concept.

The entity says "I published. Something interesting happened." and stores the event internally. The infrastructure (DbContext + OutboxPublisherJob) handles the delivery.

---

## Chapter 2 — The Outbox Pattern: Guaranteed Event Delivery

This is the most important pattern in this project. Read it carefully.

### The Problem

Consider the naive approach:

```csharp
// ❌ DANGEROUS — not atomic
await db.SaveChangesAsync();       // Step 1: Book saved to PostgreSQL ✓
await sns.PublishAsync(bookEvent); // Step 2: Event published to SNS ✓ or ✗?
```

If the application crashes between Step 1 and Step 2 — because of a power cut, a container restart, an OutOfMemoryException — the book exists in the database but the event was never published.

The email service never sends the confirmation. The search indexer never indexes the book. The analytics system never knows it was published. The system is **silently inconsistent**.

### The Solution: Write Events to the Database

```
┌─────────────────────────────────────────────────────────────────────┐
│                    ONE DATABASE TRANSACTION                          │
│                                                                      │
│   INSERT INTO books (title, isbn, ...) VALUES (...)                 │
│   INSERT INTO outbox_messages (event_type, payload) VALUES (...)    │
│                                                                      │
│   COMMIT  ← Either BOTH succeed or NEITHER does                     │
└─────────────────────────────────────────────────────────────────────┘
                            │
                            │ (milliseconds to minutes later)
                            ▼
┌─────────────────────────────────────────────────────────────────────┐
│                    OutboxPublisherJob (background)                   │
│                                                                      │
│   SELECT * FROM outbox_messages WHERE processed_at IS NULL          │
│   → Publish each message to SNS                                      │
│   → UPDATE outbox_messages SET processed_at = NOW()                 │
└─────────────────────────────────────────────────────────────────────┘
```

**Key insight**: the business data and the event are in the same database. They share the same ACID transaction. Either both are committed or neither is.

If the app crashes after the database commit, the message is still in `outbox_messages`. When the app restarts, the background job will find and publish it.

### How It's Implemented

The magic happens in `AppDbContext.SaveChangesAsync()`:

```csharp
public override async Task<int> SaveChangesAsync(CancellationToken ct = default)
{
    // Collect domain events from all tracked entities
    var entitiesWithEvents = ChangeTracker.Entries<Entity<int>>()
        .Where(e => e.Entity.DomainEvents.Any())
        .Select(e => e.Entity)
        .ToList();

    // Convert each event to an OutboxMessage
    foreach (var entity in entitiesWithEvents)
    {
        foreach (var domainEvent in entity.DomainEvents)
        {
            OutboxMessages.Add(OutboxMessage.FromDomainEvent(domainEvent));
        }
        entity.ClearDomainEvents();
    }

    // Commit: business entities + outbox messages — together
    return await base.SaveChangesAsync(ct);
}
```

This override is **invisible to handlers**. They call `Book.Publish()` and return. They never interact with the outbox. The override intercepts before the SQL runs.

---

## Chapter 3 — The TransactionBehavior: Atomic Commands

In Project 3, handlers called `SaveChangesAsync()` themselves. Project 4 moves this responsibility to the `TransactionBehavior`.

### Why Move SaveChanges to a Behaviour?

Consider a command handler that does two database operations:

```csharp
// In handler (Project 3 style — problematic)
_db.Books.Add(book);
_db.AuditLogs.Add(new AuditLog(book.Id, "Created"));
await _db.SaveChangesAsync(); // Single save — fine
```

This is fine for one operation. But what if the command is more complex?

```csharp
// Complex multi-step handler
_db.Books.Add(book);
await _db.SaveChangesAsync(); // Step 1: commit book

_db.AuditLogs.Add(new AuditLog(book.Id)); 
// ← CRASH HERE: audit log is never saved, but book exists
await _db.SaveChangesAsync(); // Step 2: commit audit log
```

The TransactionBehavior solves this: **handlers never call SaveChanges**. They queue all their changes. The behaviour wraps everything in one transaction and commits once.

```csharp
// With TransactionBehavior
// Handler does:
_db.Books.Add(book);         // Staged (not committed yet)
_db.AuditLogs.Add(auditLog); // Staged (not committed yet)
return Result.Success();     // ← Returns WITHOUT saving

// TransactionBehavior then:
await _db.SaveChangesAsync(); // Commits ALL staged changes
await transaction.CommitAsync(); // ← One atomic transaction
```

Commands opt in by implementing `ITransactionalCommand`:

```csharp
public sealed record CreateBookCommand(...) 
    : IRequest<Result<int>>, ITransactionalCommand; // ← Opts into transaction
```

Queries do NOT implement `ITransactionalCommand` — they are read-only and don't need transactions.

---

## Chapter 4 — AWS SNS and SQS: The Event Bus

### SNS — Simple Notification Service (The Broadcaster)

SNS is a pub/sub service. When you publish a message to an SNS **topic**, SNS delivers it to all **subscribers** of that topic immediately. SNS does not store messages — if no subscriber is listening when you publish, the message is gone.

```
BookLibrary → SNS topic: book-library-events → delivers to all subscribers
```

### SQS — Simple Queue Service (The Buffer)

SQS is a message queue. Messages are stored durably until a consumer reads and deletes them. If the consumer is offline, messages accumulate.

```
SNS topic: book-library-events
    ├── SQS Queue: notification-service-queue  ← Email service consumer
    └── SQS Queue: search-indexer-queue        ← Elasticsearch indexer
```

Each service has its own queue. If the email service is down, its messages wait in the queue. When it comes back, it processes them in order. The search indexer isn't affected by the email service's downtime.

### Why Use Both?

| Concern | SNS Alone | SQS Alone | SNS + SQS |
|---|---|---|---|
| Multiple consumers | ✓ | ✗ | ✓ |
| Durable storage | ✗ | ✓ | ✓ |
| Back pressure | ✗ | ✓ | ✓ |
| Message filtering | ✓ | ✗ | ✓ |

### Message Attributes for Filtering

SNS subscribers can filter which messages they receive:

```csharp
MessageAttributes = new Dictionary<string, MessageAttributeValue>
{
    ["EventType"] = new MessageAttributeValue
    {
        DataType = "String",
        StringValue = "BookPublishedEvent" // subscribers can filter on this
    }
}
```

The email service might only subscribe to `BookPublishedEvent`. The analytics service might subscribe to ALL events. Filtering happens at the SNS layer — irrelevant messages never reach the queue.

---

## Chapter 5 — LocalStack: Testing AWS Locally

LocalStack is a Docker container that emulates AWS services. The AWS SDK is completely unaware — it thinks it's talking to real AWS.

```yaml
# docker-compose.yml
localstack:
  image: localstack/localstack:3
  environment:
    - SERVICES=sns,sqs  # Only start what you need
  ports:
    - "4566:4566"       # All AWS services on one port
```

In `appsettings.json`:

```json
{
  "Aws": {
    "LocalStack": { "ServiceUrl": "http://localhost:4566" },
    "Sns": { "BookEventsTopicArn": "arn:aws:sns:us-east-1:000000000000:book-library-events" }
  }
}
```

In `Program.cs`:

```csharp
// Point the SNS client at LocalStack if configured
if (!string.IsNullOrEmpty(localstackUrl))
{
    builder.Services.AddSingleton<IAmazonSimpleNotificationService>(_ =>
    {
        var config = new AmazonSimpleNotificationServiceConfig
        {
            ServiceURL = localstackUrl, // ← Points to LocalStack instead of AWS
            AuthenticationRegion = "us-east-1"
        };
        var credentials = new BasicAWSCredentials("test", "test"); // Ignored by LocalStack
        return new AmazonSimpleNotificationServiceClient(credentials, config);
    });
}
```

In production (deployed to AWS), `Aws:LocalStack:ServiceUrl` is not set. The SDK automatically picks up the IAM role credentials from the environment. Zero code change.

---

## Chapter 6 — Structured Logging with Serilog

In development, you see human-readable log lines. In production (CloudWatch, Datadog, Grafana Loki), you need machine-readable JSON.

```csharp
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}")
    .Enrich.WithProperty("Application", "BookLibrary.CloudNative")
    .CreateLogger();
```

Each log entry is a **structured object**, not a string:

```json
{
  "Timestamp": "2025-01-15T10:23:45Z",
  "Level": "INFO",
  "Application": "BookLibrary.CloudNative",
  "SourceContext": "BookLibrary.CloudNative.Infrastructure.Messaging.SnsEventPublisher",
  "EventType": "BookPublishedEvent",
  "EventId": "a3f4-...",
  "MessageId": "sns-msg-xyz"
}
```

You can then query CloudWatch Insights:
```sql
fields @timestamp, EventType, MessageId
| filter EventType = "BookPublishedEvent"
| sort @timestamp desc
| limit 100
```

This is impossible with unstructured string logs.

---

## Chapter 7 — The Full Request Flow in Project 4

When `POST /api/books/42/publish` is called:

```
1. HTTP Request arrives at BooksController.Publish(42)
2. Controller creates: PublishBookCommand(BookId: 42)
3. Controller calls: _mediator.Send(command)

4. MediatR Pipeline begins:
   ├── LoggingBehavior: [START] PublishBookCommand
   │
   ├── ValidationBehavior: (no validator for PublishBookCommand — skips)
   │
   └── TransactionBehavior:
         ├── BeginTransactionAsync()
         ├── Calls next() → PublishBookCommandHandler.Handle()
         │     ├── Load Book from DB (with Author via Include)
         │     ├── book.Publish()
         │     │     ├── book.IsPublished = true
         │     │     └── book.DomainEvents.Add(BookPublishedEvent)
         │     └── Return Result.Success()  ← no SaveChanges here
         ├── TransactionBehavior calls SaveChangesAsync()
         │     └── AppDbContext.SaveChangesAsync() override:
         │           ├── Finds BookPublishedEvent in book.DomainEvents
         │           ├── Creates OutboxMessage{EventType="BookPublishedEvent", Payload=JSON}
         │           ├── Adds OutboxMessage to context
         │           └── base.SaveChangesAsync():
         │                 ├── UPDATE books SET is_published = true
         │                 └── INSERT INTO outbox_messages (...)
         └── CommitAsync() ← BOTH writes are now committed

5. LoggingBehavior: [END] PublishBookCommand in 42ms
6. HTTP Response: 200 OK

... 5 seconds later ...

7. OutboxPublisherJob wakes up
8. SELECT * FROM outbox_messages WHERE processed_at IS NULL
9. Deserializes BookPublishedEvent from payload JSON
10. SnsEventPublisher.PublishAsync(bookPublishedEvent)
    └── AWS SDK calls: POST https://localhost:4566/sns/Publish
        → LocalStack receives message
        → (In production: SNS delivers to subscribed SQS queues)
11. UPDATE outbox_messages SET processed_at = NOW() WHERE id = '...'
```

---

## Chapter 8 — Honest Pros and Cons

### ✅ What Project 4 Gets Right

| Strength | Why it matters |
|---|---|
| **Guaranteed delivery** | Events are committed to DB before publishing. No silent drops. |
| **At-least-once semantics** | If the app crashes, the outbox job retries on restart. |
| **Loose coupling** | Publisher knows nothing about subscribers. New subscribers = zero code change. |
| **Testable without AWS** | LocalStack emulates SNS locally. No AWS account needed. |
| **Structured observability** | JSON logs enable sophisticated queries in CloudWatch. |
| **Domain-driven design** | Business rules live in domain classes. Infrastructure is invisible. |

### ❌ The Complexity Cost

Project 4 is significantly more complex than Project 3. It requires:
- Understanding domain events, outbox pattern, aggregate roots
- Running Docker (PostgreSQL + LocalStack) for local development
- Understanding distributed systems concepts (at-least-once delivery, idempotency)
- Significantly more boilerplate code

**The rule of thumb**: use the simplest architecture that solves your problem. For a personal portfolio project, Project 1 or 2 is fine. For a startup with one service, Project 3 is great. Project 4 patterns are justified when:
- You have multiple services that need to react to each other's changes
- You cannot afford silent data loss (financial transactions, order processing)
- You need to audit everything that happens (compliance requirements)
- Your system is expected to scale significantly

### Where GrapeSeed Goes Further

GrapeSeed's architecture builds on Project 4 with:
- **Per-tenant PostgreSQL schemas**: each school's data is isolated in its own schema
- **Dead Letter Queues (DLQ)**: failed messages after 5 retries go to a DLQ for manual investigation
- **CloudWatch alarms**: alert on-call engineer when DLQ is non-empty
- **Idempotency keys**: consumers check `EventId` to avoid processing duplicates
- **Redis session caching**: O(1) JWT token revocation without DB queries

---

## Getting Started

```bash
# 1. Start PostgreSQL and LocalStack
docker compose up -d

# 2. Run the application (migrations apply automatically)
dotnet run

# 3. Open Swagger UI
# http://localhost:5000/swagger

# 4. Create an author
POST /api/authors  { "FirstName": "George", "LastName": "Orwell" }

# 5. Create a book
POST /api/books  { "Title": "1984", "Isbn": "978-0-452-28423-4", "AuthorId": 1, "PublishedYear": 1949 }

# 6. Publish the book (triggers the full outbox → SNS flow!)
POST /api/books/1/publish

# 7. Check the outbox table to see the event
# (connect to postgres and run:)
SELECT event_type, payload, processed_at FROM outbox_messages;

# 8. Within 5 seconds, check LocalStack to see the SNS publish
docker logs booklibrary_localstack
```

---

*This is the final project in the series. Return to the [Master README](../README.md) for the full architectural evolution overview.*
