# 🌱 GrapeSeed Learning Platform

> *A hands-on, book-style sample project for learning modern distributed systems, multi-tenancy, microservices, and AWS cloud engineering with .NET.*

---

## What Is This Project?

GrapeSeed is an **educational sample project** — not a production application, but a carefully crafted learning codebase. Think of it as a textbook that you can run, explore, and modify. Every file is annotated with detailed explanations written in plain English, as if a senior engineer is sitting beside you and walking you through each decision.

The story behind GrapeSeed: imagine a company that builds a **SaaS e-learning platform**. Schools and training centres (the *tenants*) subscribe and pay a monthly fee. Their students log in, watch curated video courses, and receive personalised video recommendations based on their viewing history. Behind the scenes, a collection of small, independent services powers every feature — each one deployable, scalable, and maintainable on its own.

---

## What You Will Learn

| Topic | Where to Find It |
|---|---|
| Distributed systems fundamentals | `docs/01-distributed-systems.md` |
| Multi-tenancy patterns | `docs/02-multi-tenancy.md` |
| Microservices design | `docs/03-microservices.md` |
| AWS SQS, SNS, S3, CloudFront, Lambda, MediaConvert | `docs/04-aws-services.md` |
| EF Core advanced patterns & MediatR | `docs/05-ef-core-and-mediatr.md` |
| PostgreSQL schemas & Redis caching | `docs/06-redis-and-postgres.md` |

---

## High-Level Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                        CLIENT APPS                          │
│             (Browser / Mobile / Admin Portal)               │
└───────────────────────────┬─────────────────────────────────┘
                            │ HTTPS
                            ▼
┌─────────────────────────────────────────────────────────────┐
│                      API GATEWAY                            │
│                (YARP Reverse Proxy / Ocelot)                │
│        Routes requests, validates JWT, injects tenant       │
└──┬──────────────┬──────────────┬─────────────┬─────────────┘
   │              │              │             │
   ▼              ▼              ▼             ▼
┌──────┐    ┌──────────┐  ┌──────────┐  ┌────────────────┐
│Tenant│    │ Identity │  │  Video   │  │Recommendation  │
│Service│   │ Service  │  │ Service  │  │   Service      │
└──┬───┘    └────┬─────┘  └────┬─────┘  └───────┬────────┘
   │             │             │                 │
   ▼             ▼             ▼                 ▼
┌──────────────────────────────────────────────────────────┐
│                  SHARED INFRASTRUCTURE                   │
│  PostgreSQL (per-tenant schema)  │  Redis Cache          │
│  AWS SNS/SQS Event Bus           │  CloudWatch Logs      │
│  S3 + CloudFront (Video CDN)     │  MediaConvert         │
└──────────────────────────────────────────────────────────┘
```

---

## Project Structure

```
grapeseed/
├── docs/                      ← Learning chapters (read these first!)
├── src/
│   ├── ApiGateway/            ← Entry point for all HTTP traffic
│   ├── Services/
│   │   ├── TenantService/     ← Tenant registration & Stripe payment
│   │   ├── IdentityService/   ← Student login, JWT, refresh tokens
│   │   ├── VideoService/      ← S3 upload, CloudFront streaming
│   │   └── RecommendationService/ ← Personalised video lists
│   └── SharedKernel/          ← Shared domain primitives & behaviours
├── infrastructure/
│   ├── aws/                   ← AWS SDK patterns & Lambda functions
│   └── docker/                ← Local dev environment
├── database/                  ← EF Core migrations & seed data
└── .github/workflows/         ← CI pipeline
```

---

## Suggested Reading Order

1. Start with `docs/00-introduction.md` — understand *why* we make each architectural choice.
2. Read `docs/02-multi-tenancy.md` to understand how a single codebase serves many schools.
3. Look at `src/SharedKernel/` — the foundation everything is built on.
4. Follow the *tenant registration flow*: `TenantService` → `StripePaymentService` → SNS event.
5. Follow the *student login flow*: `IdentityService` → JWT → Redis session.
6. Follow the *video streaming flow*: S3 upload → MediaConvert → CloudFront signed URL.

---

## Prerequisites (for local exploration)

```bash
# Install these to run the local stack
docker compose up -d          # Postgres + Redis + LocalStack (AWS emulator)
dotnet ef database update     # Apply EF Core migrations
```

> **Note:** This project is intentionally structured for *learning*, not production deployment. Real secrets, connection strings, and AWS credentials are replaced with clearly marked stubs.

---

## Key Design Decisions at a Glance

| Decision | Why |
|---|---|
| One PostgreSQL schema per tenant | Strong data isolation without separate databases |
| MediatR for all commands/queries | Clean separation of HTTP layer from business logic |
| Outbox pattern for events | Guarantees at-least-once delivery even if the broker goes down |
| Redis for JWT sessions | O(1) lookup for token revocation at scale |
| CloudFront signed URLs | Videos are never exposed directly; access is always controlled |
| SNS → SQS fan-out | Decouples producers from consumers; each service has its own queue |

---

*Happy learning! 🌱*
