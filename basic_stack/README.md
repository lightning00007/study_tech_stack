# 📚 The Book Library — A Four-Project Learning Series

> *"The only way to go fast is to go well."*  
> — Robert C. Martin

---

## About This Series

Four working .NET projects, all solving the same problem — managing a library of books and authors — using increasingly sophisticated techniques.

The domain stays constant. The architecture evolves. Read the projects in order, and you will see exactly why each pattern was introduced, what problem it solves, and what complexity it adds.

---

## The Four Projects at a Glance

| # | Project | Key Technology | Lines of Code | Testability |
|---|---|---|---|---|
| 1 | [Monolith](./proj1_monolith/) | Raw EF Core | ~100 | ❌ Requires DB + HTTP |
| 2 | [Layered](./proj2_layered/) | Repository + Service Layer + DI | ~300 | ✅ Unit testable (fake repo) |
| 3 | [CQRS](./proj3_cqrs/) | MediatR + FluentValidation + Result\<T\> | ~400 | ✅ Isolated handler tests |
| 4 | [Cloud Native](./proj4_cloud/) | AWS SNS/SQS + Outbox + Domain Events | ~700 | ✅ LocalStack for AWS |

---

## The Architecture Evolution

```
PROJECT 1 — Everything in one place
─────────────────────────────────────────────────────────
Request → Program.cs endpoint (validation + logic + DB) → PostgreSQL


PROJECT 2 — Separated by responsibility
─────────────────────────────────────────────────────────
Request → Controller → Service → Repository → DbContext → PostgreSQL
           (HTTP)     (Logic)    (DB query)    (ORM)


PROJECT 3 — Commands and queries, no service class
─────────────────────────────────────────────────────────
Request → Controller → MediatR → LoggingBehavior
                                 → ValidationBehavior
                                   → Handler → DbContext → PostgreSQL


PROJECT 4 — Domain events, guaranteed delivery, cloud messaging
─────────────────────────────────────────────────────────
Request → Controller → MediatR → LoggingBehavior
                                 → ValidationBehavior
                                   → TransactionBehavior
                                     → Handler → Domain (raises events)
                                       ↕
                                       DbContext (intercepts events)
                                       ↕ atomic transaction
                                       books table + outbox_messages table
                                                       ↕
                                              OutboxPublisherJob (background)
                                                       ↕
                                                   AWS SNS → SQS subscribers
```

---

## Reading Order

**For beginners**: Start at Project 1. Read the GUIDE.md before looking at the code. The guide explains every concept in plain English before you encounter it in C#.

**For intermediate developers**: Start at Project 2 or 3 depending on your experience. Projects 1-2 cover DI and repository patterns; Projects 3-4 cover CQRS and cloud messaging.

**For experienced developers**: Go directly to Project 4 and work backwards to understand the motivations for each pattern.

---

## Key Concepts by Project

### Project 1 teaches:
- Entity Framework Core fundamentals (DbContext, DbSet, migrations)
- Data Annotations for schema configuration
- Minimal API and Dependency Injection basics
- Change Tracking and SaveChanges

### Project 2 teaches:
- Separation of Concerns (layered architecture)
- Dependency Injection (why and how)
- Repository pattern (abstraction over data access)
- Service layer (centralised business logic)
- Fluent API vs Data Annotations

### Project 3 teaches:
- CQRS (Command Query Responsibility Segregation)
- MediatR and the Mediator pattern
- Pipeline behaviours (cross-cutting concerns)
- FluentValidation (declarative, testable validation)
- Result\<T\> — Railway-Oriented Programming
- Vertical Slice Architecture

### Project 4 teaches:
- Aggregate Roots and Domain Entities (DDD)
- Domain Events (how entities communicate without coupling)
- Transactional Outbox Pattern (guaranteed event delivery)
- AWS SNS (pub/sub messaging)
- AWS SQS (durable message queuing)
- LocalStack (local AWS development without AWS account)
- Structured Logging with Serilog
- Background Services (IHostedService)
- Transaction management via pipeline behaviour

---

## How This Relates to GrapeSeed

GrapeSeed (in the `../grapeseed/` folder) is the production-grade application this series prepares you to read. Comparing the two:

| Concept | This Series | GrapeSeed |
|---|---|---|
| Domain entities | `Book`, `Author` | `Tenant`, `Video`, `Student` |
| Domain events | `BookPublished` | `TenantRegistered`, `TenantActivated` |
| Outbox pattern | `OutboxMessage` | `OutboxMessage` (identical!) |
| Pipeline behaviours | Logging, Validation, Transaction | Logging, Validation, Transaction |
| AWS SNS | Single topic | Multiple topics (one per event type) |
| Multi-tenancy | ❌ Not covered | ✅ Per-tenant PostgreSQL schemas |
| Redis caching | ❌ Not covered | ✅ JWT session caching |
| CloudFront CDN | ❌ Not covered | ✅ Video streaming |

After working through all four projects, reading `grapeseed/src/Services/TenantService/` will feel familiar rather than overwhelming.

---

## Prerequisites

```bash
# Install .NET 8 SDK
# https://dotnet.microsoft.com/download/dotnet/8

# Install Docker Desktop (for Projects 4 only)
# https://www.docker.com/products/docker-desktop

# For Projects 1-3: just PostgreSQL
# docker run -e POSTGRES_PASSWORD=postgres -p 5432:5432 -d postgres:16

# For Project 4: PostgreSQL + LocalStack
# cd proj4_cloud
# docker compose up -d
```

---

## Running Each Project

```bash
# Project 1
cd proj1_monolith
dotnet run
# → http://localhost:5000/swagger

# Project 2
cd proj2_layered
dotnet run
# → http://localhost:5001/swagger

# Project 3
cd proj3_cqrs
dotnet run
# → http://localhost:5002/swagger

# Project 4
cd proj4_cloud
docker compose up -d   # start PostgreSQL + LocalStack
dotnet run
# → http://localhost:5003/swagger
```

---

*Happy reading! For questions about GrapeSeed's production patterns, read `grapeseed/docs/` alongside the code.*
