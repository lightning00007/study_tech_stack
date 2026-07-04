# System Architecture Mastery
### *A Field Guide for Engineers Building the GrapeSEED English Education Platform*

> *"Good architecture is not about using the fanciest technologies. It is about making the right tradeoffs at the right time."*
> — Every senior engineer who has ever been paged at 3 AM

---

## About GrapeSEED — Know What You're Building

Before writing a single line of code, you should understand the product and the company deeply. Architecture decisions must serve the business and the users, not the other way around.

### What Is GrapeSEED?

**GrapeSEED** is an English oral language acquisition program for children aged 4–12, designed to help children learn English the same natural way they learned their native language — through continuous, meaningful exposure, not through memorizing grammar rules.

The program was born out of decades of teaching at **MeySen Academy in Japan**, founded in 1967 by American educators **John Broman and Daniel Fanger**. After years of observing how children naturally acquire language, they developed a methodology grounded in research on language acquisition, early childhood education, and brain development. GrapeSEED grew out of that work into a structured, globally-deployed curriculum.

GrapeSEED currently operates in approximately **18–19 countries** including Japan, South Korea, Vietnam, Thailand, Cambodia, Malaysia, Mongolia, China, Myanmar, Russia, Albania, Italy, and the United States. Its users are preschools, kindergartens, and language centers — institutions that partner with GrapeSEED to offer structured English programs to young children.

### The Teaching Philosophy That Drives the Tech

Understanding the *why* behind GrapeSEED's methodology helps you understand what the system must do technically:

- **Natural acquisition over memorization:** Children learn by hearing the same songs, stories, chants, and phrases repeatedly over weeks. This means the system must serve **audio and video content reliably at scale**, across devices, often in countries with inconsistent internet infrastructure.
- **Controlled vocabulary progression:** Content is carefully sequenced — you cannot skip a unit. The system must enforce curriculum order and track progress against a specific sequence, not just any collection of lessons.
- **Teacher-led classroom + at-home practice:** Learning is split between a teacher-led classroom session (using **GrapeSEED Nexus**) and daily home practice (using the **GrapeSEED Student App / REP**). Your backend serves both contexts simultaneously.

### The GrapeSEED Technology Ecosystem

GrapeSEED is not a single app — it is an ecosystem of interconnected products:

| Product | Who Uses It | What It Does |
|---------|------------|--------------|
| **GrapeSEED Student App (REP)** | Students (ages 4–12) | Daily repeated exposure practice at home. Playlists of songs, stories, chants. iOS, Android, browser. |
| **GrapeSEED Nexus** | Teachers | In-classroom tablet app. Presents lessons on smartboards, manages attendance, assigns active-learn content to student playlists. |
| **GrapeSEED Connect** | Teachers + Students | Web/app video conferencing for remote/hybrid classes. Integrated curriculum materials, virtual stickers, live annotation. |
| **GrapeSEED School Portal** | School administrators | Manage campus structure, configure licenses, monitor class and student progress, order materials. |
| **Parent Portal** | Parents | Track their child's daily REP completion and progress. |

As a backend engineer, your job is to power **all of these products** through a shared set of APIs and services.

### The Business Model — B2B School Licensing

GrapeSEED is a **B2B company**. It does not sell directly to parents or students. Instead, it partners with schools and language centers, which then deliver GrapeSEED to enrolled children:

```
GrapeSEED (HQ) ── licenses curriculum + software ──► Partner School
                                                           │
                                                     School deploys
                                                     to enrolled children
                                                           │
                                                    ┌──────┴──────────┐
                                                 Teachers          Students
                                                (Nexus app)     (Student App)
```

Each partner school is a **tenant** in your system. They have:
- A set of **student licenses** purchased from GrapeSEED (one license = one enrolled student)
- **Campus and class structures** they manage via the School Portal
- Their own teachers and administrator accounts
- Configuration for which GrapeSEED content they have licensed (e.g., Units 1–6 only)
- Their own delivery model preference: Offline, Online (Connect), or Hybrid (Nexus + Connect)

This B2B licensing model is the core driver for the multi-tenant architecture described in Chapter 2.

---

## How to Read This Book

Each chapter is **self-contained** but builds logically on the previous one. Read in order if you are new to all topics.

### Recommended Reading Path

```
Chapter 1: Distributed Systems       ← Why one server will never be enough
        ↓
Chapter 2: Multi-Tenant Architecture  ← How to serve many partner schools safely
        ↓
Chapter 3: High Load Systems          ← How to handle daily traffic spikes
        ↓
Chapter 4: Microservices & MediatR    ← How to organize the codebase as GrapeSEED scales
```

Every chapter follows this structure:

1. **The Real-World Problem** — A specific scenario from GrapeSEED's context
2. **Core Concepts** — Plain-English explanations with analogies
3. **The Theory** — Formal models and patterns
4. **C# in Practice** — Real, commented code using your actual tech stack
5. **GrapeSEED Scenario** — How this specifically applies to the platform
6. **Decision Guide** — When to use, when to avoid, common mistakes

---

## Tech Stack Reference

All code examples are grounded in the real GrapeSEED stack:

| Layer | Technology | Purpose |
|-------|-----------|---------|
| Cloud Platform | **Amazon Web Services (AWS)** | Hosting, managed compute, storage |
| Primary Database | **PostgreSQL on Amazon RDS** | Core transactional data (students, progress, licenses) |
| Reporting Database | **SQL Server on Amazon RDS** | Complex T-SQL reporting, school management analytics |
| ORM | **Entity Framework Core** | Database access across both database providers |
| Cache | **Amazon ElastiCache for Redis** | Distributed cache: content, sessions, school config |
| In-Process Messaging | **MediatR** | CQRS — commands/queries/notifications within a service |
| Async Messaging | **Amazon SQS / SNS** | Cross-service events (progress sync, notifications) |
| CDN | **Amazon CloudFront** | Lesson audio/video delivery to students globally |
| Media Storage | **Amazon S3** | Lesson songs, stories, video files, certificates |
| Container Orchestration | **Amazon ECS Fargate** | Stateless service deployment |
| API Gateway | **AWS API Gateway / ALB** | Single entry point, routing, JWT auth |
| Observability | **AWS CloudWatch + X-Ray** | Logs, metrics, distributed tracing |
| Secrets | **AWS Secrets Manager** | DB connection strings, API keys |

---

## Chapters at a Glance

### 📡 [Chapter 1 — Distributed Systems](./book_ch1_distributed_systems.md)

*"How do you build a system that keeps working even when parts of it — or parts of AWS — fail?"*

The 8 Fallacies of Distributed Computing, the CAP Theorem, consistency models, circuit breakers with Polly, and distributed caching with Amazon ElastiCache.

**GrapeSEED Angle:** How lesson audio and video content is served reliably to students in Vietnam and South Korea while they practice daily English exercises using the Student App (REP) — including offline sync scenarios.

---

### 🏫 [Chapter 2 — Multi-Tenant Architecture](./book_ch2_multi_tenant_systems.md)

*"How do you serve hundreds of partner schools — each with their own students, licenses, and curriculum — from one shared platform?"*

The three tenancy models, EF Core global query filters, Row-Level Security in PostgreSQL and SQL Server, and MediatR pipeline behaviors for tenant context injection.

**GrapeSEED Angle:** A school in Hanoi and a school in Seoul both use GrapeSEED. They must never see each other's student data, progress records, or license information — enforced architecturally, not by convention.

---

### ⚡ [Chapter 3 — High Load Systems](./book_ch3_high_load_systems.md)

*"How do you keep GrapeSEED fast when thousands of children simultaneously open their daily lesson playlist?"*

CloudFront for global audio/video delivery, ElastiCache caching, ECS Auto Scaling, RDS read replicas, SQS async processing, rate limiting, and Hangfire background jobs.

**GrapeSEED Angle:** Back-to-school season. Hundreds of partner schools in multiple time zones go live simultaneously. The Student App (REP) must serve daily playlists to tens of thousands of concurrent children with sub-second load times.

---

### 🧩 [Chapter 4 — Microservices & MediatR](./book_ch4_microservices.md)

*"How do teams independently build, deploy, and scale GrapeSEED's School Portal, Student App, Nexus, and Connect — while MediatR keeps each service's code clean?"*

Full MediatR CQRS pattern (commands, queries, notifications, pipeline behaviors), GrapeSEED's service decomposition, AWS API Gateway, SQS/SNS event bus, Saga pattern for student enrollment, and AWS X-Ray tracing.

**GrapeSEED Angle:** The Student App team, the School Portal team, and the Nexus team all deploy independently. MediatR ensures every handler inside each service is clean, testable, and consistent.

---

## The GrapeSEED Platform We Are Building

```
┌──────────────────────────────────────────────────────────────────────────┐
│              GrapeSEED Technology Ecosystem (AWS Infrastructure)          │
│                                                                            │
│  ┌─────────────────┐ ┌───────────────┐ ┌──────────────┐ ┌────────────┐  │
│  │  Student App    │ │ Nexus Teacher │ │   Connect    │ │  School    │  │
│  │  (REP)          │ │  (iOS/Android)│ │  (Video Call)│ │  Portal    │  │
│  │  iOS/Android/   │ │   Classroom   │ │   Remote     │ │  Web Admin │  │
│  │  Browser        │ │   Management  │ │   Classes    │ │  Dashboard │  │
│  └────────┬────────┘ └──────┬────────┘ └──────┬───────┘ └─────┬──────┘  │
│           │                 │                 │               │           │
│  ┌────────▼─────────────────▼─────────────────▼───────────────▼────────┐ │
│  │              Amazon CloudFront (CDN + WAF + DDoS Protection)         │ │
│  └─────────────────────────────────────┬────────────────────────────────┘ │
│                                         │                                  │
│  ┌──────────────────────────────────────▼────────────────────────────────┐│
│  │            AWS API Gateway / Application Load Balancer                ││
│  │       JWT Auth · Routing · Rate Limiting · SSL Termination            ││
│  └───────┬─────────────┬──────────────────┬──────────────────┬───────────┘│
│          │             │                  │                  │             │
│  ┌───────▼───┐ ┌───────▼────┐ ┌──────────▼──┐ ┌────────────▼──┐          │
│  │Identity   │ │Content     │ │  Progress   │ │  School       │          │
│  │Service    │ │Service     │ │  Service    │ │  Portal Svc   │          │
│  │(ECS)      │ │(ECS)       │ │  (ECS)      │ │  (ECS)        │          │
│  │           │ │            │ │             │ │               │          │
│  │Students,  │ │Units,      │ │REP progress,│ │Tenants,       │          │
│  │teachers,  │ │lessons,    │ │Scores,      │ │licenses,      │          │
│  │auth, JWT  │ │playlists,  │ │certificates │ │school config  │          │
│  │           │ │media refs  │ │             │ │               │          │
│  └─────┬─────┘ └──────┬─────┘ └──────┬──────┘ └───────┬───────┘          │
│        │              │              │                 │                   │
│  ┌─────▼──┐    ┌───────▼──┐  ┌───────▼───┐   ┌────────▼───┐              │
│  │RDS PG  │    │RDS PG    │  │ RDS PG    │   │RDS PG      │              │
│  │        │    │(+ S3 for │  │           │   │            │              │
│  │        │    │ media)   │  │           │   │            │              │
│  └────────┘    └──────────┘  └───────────┘   └────────────┘              │
│                                                                            │
│  ┌──────────────┐  ┌───────────────────────────────────────────────────┐  │
│  │ ElastiCache  │  │  Notification + Analytics Services                │  │
│  │ (Redis)      │  │  NotifySvc: SQS + SES/SNS (email/push to parents) │  │
│  │              │  │  AnalyticsSvc: RDS SQL Server + QuickSight        │  │
│  └──────────────┘  └───────────────────────────────────────────────────┘  │
│                                                                            │
│  ┌─────────────────────────────────────────────────────────────────────┐  │
│  │    Amazon SQS / SNS  (Cross-Service Events: progress, enrollments)  │  │
│  └─────────────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────────────┘
```

---

## Why Both PostgreSQL and SQL Server?

Most services use **PostgreSQL on RDS** — it's open-source, cost-effective, supports Row-Level Security for multi-tenancy, and has excellent EF Core support.

**SQL Server on RDS** is used specifically for the **Analytics Service**, which powers the School Portal's management dashboards and GrapeSEED's internal business reporting:
- School administrators expect familiar tabular reports (enrollment numbers, license usage, completion rates by class)
- The analytics team uses complex T-SQL queries: window functions, CTEs, SSRS-style aggregations
- GrapeSEED's internal business teams use SQL Server tools for ad-hoc analysis

EF Core abstracts both databases cleanly — swapping providers is a one-line change in `Program.cs`.

---

## Prerequisites

This book assumes you:
- Can read and write **C#** at an intermediate level
- Have used **Entity Framework Core** at least once
- Have basic familiarity with **REST APIs**
- Know what **Docker containers** are
- Have encountered **MediatR** (even just the concept of CQRS)

You do **not** need to be an AWS expert — cloud-specific concepts are explained as they appear.

---

*→ Start reading: [Chapter 1 — Distributed Systems](./book_ch1_distributed_systems.md)*
