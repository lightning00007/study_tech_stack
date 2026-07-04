# System Architecture Mastery
### *A Field Guide for Engineers Building the Grapeseed English Education Program*

> *"Good architecture is not about using the fanciest technologies. It is about making the right tradeoffs at the right time."*
> — Every senior engineer who has ever been paged at 3 AM

---

## Welcome to This Book

You are building the **Grapeseed English Education Program** — a platform that delivers structured English lessons, interactive videos, quizzes, and school management tools to institutions around the world. Grapeseed is not a simple CRUD application. It needs to:

- Serve **students and teachers globally** across multiple continents and time zones
- Keep **school data strictly separated** — a student in one school must never see data from another
- Stay online **even during AWS region issues**, network problems, or database failures
- **Scale automatically** when exam season arrives and traffic spikes
- Support **both PostgreSQL and SQL Server** databases for different parts of the system
- Be maintained by teams using a consistent, organized code structure through **MediatR**

To build Grapeseed at this level of quality, you need to understand four foundational pillars of modern backend engineering. This book teaches all four, with every example grounded in **the Grapeseed platform, on AWS, using C# with EF Core, Redis, and MediatR**.

---

## How to Read This Book

Each chapter is **self-contained**, but they build on each other logically. Read in order if you are new to all topics. Jump to a chapter if you need a specific skill.

### Recommended Reading Path

```
Chapter 1: Distributed Systems
        ↓
Chapter 2: Multi-Tenant Architecture
        ↓
Chapter 3: High Load Systems
        ↓
Chapter 4: Microservices & MediatR
```

Every chapter follows the same structure:

1. **The Real-World Problem** — Why does this topic exist? What breaks without it?
2. **Core Concepts** — Plain-English explanations with analogies
3. **The Theory** — Formal models and patterns
4. **C# in Practice** — Real, commented code using your actual tech stack
5. **Grapeseed Scenario** — How this applies specifically to your system
6. **Decision Guide** — When to use, when to avoid, common mistakes

---

## Tech Stack Reference

Throughout this book, all code examples are grounded in the actual Grapeseed stack:

| Layer | Technology | Purpose |
|-------|-----------|---------|
| Cloud Platform | **Amazon Web Services (AWS)** | Hosting, compute, managed services |
| Primary Database | **PostgreSQL on Amazon RDS** | Core transactional data |
| Enterprise Database | **SQL Server on Amazon RDS** | Reporting, legacy integrations |
| ORM | **Entity Framework Core** | Database access layer |
| Cache | **Amazon ElastiCache for Redis** | Distributed caching, sessions |
| In-Process Messaging | **MediatR** | CQRS commands/queries within a service |
| Async Messaging | **Amazon SQS / SNS** | Cross-service event delivery |
| CDN | **Amazon CloudFront** | Global content delivery |
| Container Orchestration | **Amazon ECS Fargate** | Stateless service deployment |
| API Gateway | **AWS API Gateway / ALB** | Single entry point, routing, auth |
| Observability | **AWS CloudWatch + X-Ray** | Logs, metrics, distributed tracing |
| Secrets | **AWS Secrets Manager** | Connection strings, API keys |
| File Storage | **Amazon S3** | Video files, images, PDFs |

---

## Chapters at a Glance

### 📡 [Chapter 1 — Distributed Systems](./book_ch1_distributed_systems.md)

*"How do you build a system that keeps working even when parts of it — or parts of AWS — fail?"*

The foundation of everything else. You will learn why a single server is never enough, the surprising problems that come with spreading work across machines, and how to design for failure. Topics: the 8 Fallacies of Distributed Computing, the CAP Theorem, consistency models, circuit breakers with Polly, and distributed caching with Amazon ElastiCache.

**Grapeseed Angle:** How lesson content is served reliably to students in Thailand, Brazil, and South Korea — simultaneously — using AWS infrastructure.

---

### 🏫 [Chapter 2 — Multi-Tenant Architecture](./book_ch2_multi_tenant_systems.md)

*"How do you build one system that serves hundreds of different schools, each believing they have their own private platform?"*

Multi-tenancy is the art of serving many customers from one codebase while keeping their data completely isolated. This chapter covers the three tenancy models, EF Core global query filters for automatic data isolation, Row-Level Security in PostgreSQL and SQL Server, MediatR pipeline behaviors for tenant injection, and AWS-specific tenant storage strategies.

**Grapeseed Angle:** School A in Bangkok and School B in São Paulo both use the same platform, but they can never see each other's students, teachers, or lesson content.

---

### ⚡ [Chapter 3 — High Load Systems](./book_ch3_high_load_systems.md)

*"How do you keep Grapeseed fast when thousands of students and teachers log in at the same moment?"*

This chapter teaches horizontal scaling on ECS Fargate, load balancing with AWS Application Load Balancer, multi-layer caching with CloudFront and ElastiCache, RDS read replicas, async processing with SQS, rate limiting, and background jobs. AWS Auto Scaling ensures you have exactly the capacity you need — no more, no less.

**Grapeseed Angle:** National English examinations day. Traffic spikes 20x in 60 seconds. The platform absorbs it without manual intervention.

---

### 🧩 [Chapter 4 — Microservices & MediatR](./book_ch4_microservices.md)

*"How do teams build, deploy, and scale different parts of Grapeseed independently — and how does MediatR keep each service's code clean and organized?"*

This chapter covers Domain-Driven Design and bounded contexts for Grapeseed's services, **MediatR CQRS pattern** (commands, queries, notifications, pipeline behaviors), AWS API Gateway routing, Amazon SQS/SNS for cross-service events, the Saga pattern for distributed transactions, and distributed tracing with AWS X-Ray.

**Grapeseed Angle:** The LessonService, VideoService, ProgressService, NotificationService — each independently deployable on ECS Fargate, each internally organized with MediatR.

---

## The Grapeseed System We Are Building

```
┌─────────────────────────────────────────────────────────────────────┐
│                    Grapeseed English Education Program               │
│                        (AWS Infrastructure)                          │
│                                                                       │
│  ┌──────────┐   ┌──────────┐   ┌─────────────────────────────┐     │
│  │ Schools  │   │ Teachers │   │  Students (Lesson+Video+Quiz)│     │
│  │(Tenants) │   │  Portal  │   └─────────────────────────────┘     │
│  └──────────┘   └──────────┘                                        │
│        │              │                   │                          │
│  ┌─────▼──────────────▼───────────────────▼──────────────────────┐  │
│  │          Amazon CloudFront (CDN + DDoS Protection)             │  │
│  └─────────────────────────┬──────────────────────────────────────┘  │
│                             │                                         │
│  ┌──────────────────────────▼──────────────────────────────────────┐ │
│  │       AWS API Gateway / Application Load Balancer               │ │
│  │       (Auth, Routing, Rate Limiting, SSL Termination)           │ │
│  └──────┬───────────┬──────────────┬───────────────┬──────────────┘ │
│         │           │              │               │                 │
│  ┌──────▼──┐  ┌─────▼──┐  ┌───────▼──┐  ┌────────▼──┐             │
│  │Identity │  │Lesson  │  │  Video   │  │ Progress  │             │
│  │Service  │  │Service │  │ Service  │  │ Service   │             │
│  │(ECS)    │  │(ECS)   │  │  (ECS)   │  │  (ECS)    │             │
│  └────┬────┘  └───┬────┘  └────┬─────┘  └─────┬─────┘             │
│       │           │             │               │                   │
│  ┌────▼──┐  ┌─────▼──┐  ┌──────▼───┐  ┌────────▼──┐               │
│  │RDS PG │  │RDS PG  │  │  S3 +    │  │ RDS PG    │               │
│  │       │  │        │  │CloudFront│  │           │               │
│  └───────┘  └────────┘  └──────────┘  └───────────┘               │
│                                                                       │
│  ┌────────────────────────────────────────────────────────────────┐  │
│  │  Amazon SQS / SNS  (Async Event Messaging Between Services)    │  │
│  └────────────────────────────────────────────────────────────────┘  │
│                                                                       │
│  ┌──────────────────┐   ┌───────────────────────────────────────┐   │
│  │ ElastiCache Redis│   │ RDS SQL Server (Reporting / Analytics) │   │
│  └──────────────────┘   └───────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────────┘
```

---

## Why Both PostgreSQL and SQL Server?

A common question when looking at the Grapeseed stack: *"Why do we have two databases?"*

**PostgreSQL on RDS** is used for the core microservices (Identity, Lesson, Progress, etc.):
- Open source, no per-core licensing cost
- Excellent support for JSON columns (flexible lesson content structures)
- Row-Level Security for multi-tenancy
- Great EF Core support, performant at scale

**SQL Server on RDS** is used for:
- **Enterprise reporting** — many school district IT administrators have existing SSRS (SQL Server Reporting Services) integrations and expect SQL Server
- **Legacy data migration** — some school systems exported data from SQL Server-based LMS systems
- **Analytics dashboards** — the Grapeseed Analytics Service uses complex T-SQL queries, CTEs, and window functions that the analytics team is most productive with in SQL Server

EF Core abstracts both databases elegantly — you can switch providers with one line in `Program.cs`, and most of your code remains identical.

---

## Prerequisites

This book assumes you:
- Can read and write **C#** at an intermediate level
- Have used **Entity Framework Core** at least once
- Have basic familiarity with **REST APIs**
- Know what **Docker containers** are
- Have heard of **MediatR** (even if you haven't used it deeply yet)

You do **not** need to be an AWS expert — cloud-specific concepts are explained as they appear.

---

*→ Start reading: [Chapter 1 — Distributed Systems](./book_ch1_distributed_systems.md)*
