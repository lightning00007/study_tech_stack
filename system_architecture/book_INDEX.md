# System Architecture Mastery
### *A Field Guide for Engineers Building World-Class Education Platforms*

> *"Good architecture is not about using the fanciest technologies. It is about making the right tradeoffs at the right time."*
> — Every senior engineer who has ever been paged at 3 AM

---

## Welcome to This Book

You have just joined a team building an **English Language Education System** — a platform that delivers lessons, videos, quizzes, and management tools to schools all over the world. This is not a simple CRUD application. It needs to:

- Serve **millions of students** in dozens of countries, simultaneously
- Keep **school data strictly separated** (a student in Tokyo should never see data from a school in London)
- Stay online **even when servers crash**, network cables are cut, or a data center floods
- **Scale up instantly** when exam season arrives and traffic explodes
- Be maintained by **many teams in parallel** without stepping on each other

To build something like this, you need to understand four foundational pillars of modern backend engineering. This book teaches all four of them, from first principles to production code.

---

## How to Read This Book

Each chapter is **self-contained**, but they build on each other logically. If you are brand new to all these topics, read in order. If you already know some concepts, feel free to jump around.

### Recommended Reading Path

```
Chapter 1: Distributed Systems
        ↓
Chapter 2: Multi-Tenant Architecture
        ↓
Chapter 3: High Load Systems
        ↓
Chapter 4: Microservices
```

Every chapter follows the same structure:

1. **The Real-World Problem** — Why does this topic exist? What breaks without it?
2. **Core Concepts** — Plain-English explanations with analogies
3. **The Theory** — The formal models and patterns
4. **C# in Practice** — Real, commented code you can adapt
5. **Education Platform Scenario** — How this applies specifically to your job
6. **Decision Guide** — When to use, when to avoid, common mistakes

---

## Chapters at a Glance

### 📡 [Chapter 1 — Distributed Systems](./book_ch1_distributed_systems.md)

*"How do you build a system that works even when parts of it fail?"*

The foundation of everything else. You will learn what happens when a single server is no longer enough — why software needs to be spread across multiple machines, and all the surprising problems that come with that decision. Topics include: the 8 Fallacies of Distributed Computing, the CAP Theorem, consistency models, fault tolerance, and circuit breakers.

**Education Platform Angle:** How lesson content is served reliably to students in Vietnam, Brazil, and Germany — all at the same time.

---

### 🏫 [Chapter 2 — Multi-Tenant Architecture](./book_ch2_multi_tenant_systems.md)

*"How do you build one system that serves hundreds of different schools, each thinking they have their own private platform?"*

Multi-tenancy is the art of serving many customers from one codebase while keeping their data completely isolated. This chapter covers the three tenancy models, row-level security, tenant context propagation, and how to build it in C# with Entity Framework Core.

**Education Platform Angle:** School A in Singapore and School B in Canada both use the same platform — but they can never see each other's students, teachers, or lesson plans.

---

### ⚡ [Chapter 3 — High Load Systems](./book_ch3_high_load_systems.md)

*"How do you keep your system fast and available when 100,000 students log in at the same moment?"*

High load engineering is about anticipating bottlenecks before they become outages. This chapter teaches horizontal scaling, load balancing, multi-layer caching (CDN, API cache, database cache), database read replicas, sharding, and async message processing.

**Education Platform Angle:** The national English exam is tomorrow. Every student in the country opens the practice platform at 8:00 AM. Your servers do not collapse.

---

### 🧩 [Chapter 4 — Microservices](./book_ch4_microservices.md)

*"How do you organize a large team of developers so they can build, deploy, and scale different parts of the system independently?"*

Microservices is an architectural style that breaks a large application into small, focused services. This chapter covers Domain-Driven Design, bounded contexts, service communication (REST vs. gRPC vs. events), API gateways, distributed tracing, and the Saga pattern for distributed transactions.

**Education Platform Angle:** The User Service, Lesson Service, Video Service, Progress Service, and Notification Service — each owned by a different team, each deployable independently, all working together seamlessly.

---

## The Education Platform We Are Building

Throughout this book, we refer to a fictional-but-realistic platform called **LinguaLearn**. Here is a high-level picture of what it does:

```
┌──────────────────────────────────────────────────────────────────┐
│                         LinguaLearn Platform                      │
│                                                                    │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────────┐  │
│  │   Schools   │  │  Teachers   │  │        Students          │  │
│  │  (Tenants)  │  │   Portal    │  │  (Lesson + Video + Quiz) │  │
│  └──────┬──────┘  └──────┬──────┘  └────────────┬────────────┘  │
│         │                │                       │               │
│  ┌──────▼──────────────────────────────────────────────────────┐ │
│  │              API Gateway (Single Entry Point)               │ │
│  └──────┬──────────────────────────────────────────────────────┘ │
│         │                                                         │
│  ┌──────▼──────┐  ┌──────────────┐  ┌──────────────┐            │
│  │ UserService │  │LessonService │  │ VideoService │            │
│  └─────────────┘  └──────────────┘  └──────────────┘            │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐           │
│  │ProgressSvc   │  │ NotifySvc    │  │ AnalyticsSvc │           │
│  └─────────────┘  └──────────────┘  └──────────────┘            │
│                                                                    │
│  ┌─────────────────────────────────────────────────────────────┐ │
│  │          Message Bus (Async Event Communication)             │ │
│  └─────────────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────────┘
```

---

## Prerequisites

This book assumes you:
- Can read and write **C#** at an intermediate level
- Have basic familiarity with **REST APIs** and **databases**
- Have used **Entity Framework Core** at least once
- Are comfortable with basic **Docker** concepts (containers, images)

You do **not** need to be an expert in distributed systems — that is what this book is for.

---

## A Note on Learning

Every concept in this book was invented to solve a **real, painful problem** that engineers encountered when building large systems. As you read, always ask yourself: *"What problem does this solve? When would I actually reach for this?"*

Architecture patterns are not trophies to collect. They are tools. The best engineers know not just how to use them, but **when to use them** — and just as importantly, when to leave them on the shelf.

Let's begin.

---

*→ Start reading: [Chapter 1 — Distributed Systems](./book_ch1_distributed_systems.md)*
