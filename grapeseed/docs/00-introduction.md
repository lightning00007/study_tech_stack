# Chapter 0 — Introduction: Why We Build This Way

> *"Software architecture is the set of significant decisions about the organisation of a software system."*  
> — Philippe Kruchten

---

## 0.1 The Problem We Are Solving

Imagine you are the CTO of GrapeSeed, a startup that sells an e-learning platform to schools
and training centres. You have just landed your first three customers. Each of them has different
branding, different teachers, different students, and different content libraries. You could build
three separate applications — but that would mean tripling every bug fix and every new feature.

Instead, you build one platform that knows how to *wear different hats*. When a request arrives
from School A, the system automatically scopes every database query, every file upload, and every
notification to School A's data — with zero chance of accidentally showing School B's videos to
School A's students. This is **multi-tenancy**: one codebase, many isolated customers.

---

## 0.2 Why Microservices?

In the early days, you might build a single application — a *monolith* — that handles everything:
tenant registration, authentication, video storage, and recommendations. This is perfectly
reasonable for a new product. However, as GrapeSeed grows, problems emerge:

- The team that works on the video player blocks the team working on billing, because they share
  the same codebase and deployment pipeline.
- A bug in the recommendation engine can crash the login page if they live in the same process.
- You cannot independently scale the video streaming service during peak hours without also
  scaling the billing service, which wastes money.

Microservices solve these problems by splitting the application into small, autonomous services
that communicate over a network. Each service owns its own database, its own deployment, and its
own team. The cost is increased complexity in networking, observability, and data consistency —
which is exactly what the rest of this project teaches you to manage.

```
Monolith                    Microservices
──────────                  ─────────────────────────────────────
┌──────────────────┐        ┌────────┐  ┌──────────┐  ┌───────┐
│  Auth + Videos   │        │ Auth   │  │  Videos  │  │ Recs  │
│  + Tenants +     │  ──►   │Service │  │ Service  │  │Service│
│  Recommendations │        └────────┘  └──────────┘  └───────┘
└──────────────────┘        Independently deployable, scalable
```

---

## 0.3 The Role of AWS

GrapeSeed runs on Amazon Web Services. Here is what each AWS service does in our story:

| AWS Service | Role in GrapeSeed |
|---|---|
| **S3** | Stores raw video files uploaded by teachers |
| **MediaConvert** | Transcodes videos into HLS format for adaptive streaming |
| **CloudFront** | Delivers videos globally via CDN; generates signed URLs |
| **SQS** | Queues for each service's incoming events (reliable, at-least-once) |
| **SNS** | Broadcasts events from publishers to multiple SQS subscribers |
| **Lambda** | Serverless functions triggered by S3 events (video ready → notify) |
| **CloudWatch** | Centralised logs, metrics, and alarms across all services |

---

## 0.4 The Domain in Plain Language

Before writing a single line of code, a good engineer understands the *domain* — the real-world
problem they are modelling. Here is GrapeSeed's domain in a single paragraph:

> A **Tenant** is a school or training centre that pays a monthly subscription (a **Plan**).
> When a Tenant registers, they go through a **Payment** flow. Once approved, they can create
> **Students** who can log in to the platform. Students watch **Videos** that belong to their
> Tenant's library. The platform tracks each student's **WatchHistory** and uses it to generate
> personalised **Recommendations**.

Every class, database table, and API endpoint in this project maps back to one of these nouns.
This mapping is called the **Ubiquitous Language** — a shared vocabulary between developers and
business stakeholders, popularised by Domain-Driven Design (DDD).

---

## 0.5 How to Use This Codebase

Each service follows the same layered structure, which makes it easy to navigate once you learn it:

```
ServiceName/
├── Domain/           ← Pure C# business logic, no framework dependencies
├── Application/      ← MediatR commands, queries, and pipeline behaviours
├── Infrastructure/   ← Database, AWS SDK, external APIs, messaging
└── Api/              ← ASP.NET Core controllers — thin adapters only
```

This structure is called **Clean Architecture** (Robert C. Martin, 2017). The rule is:
dependencies always point inward. The Domain knows nothing about the database. The Application
knows nothing about HTTP. The Api knows about everything, but only to wire them together.

---

## 0.6 A Note on "Production Readiness"

This project deliberately prioritises **clarity over conciseness**. Real production code would
have shorter variable names, fewer comments, and more aggressive use of framework conventions.
Here, every non-obvious line is explained — because the goal is learning, not shipping.

Look for these annotation tags throughout the code:

```csharp
// 📖 CONCEPT: explains a design pattern or architectural idea
// ⚠️  GOTCHA: warns about a common mistake or pitfall
// 🔗 SEE ALSO: points to a related file or doc chapter
// 💡 WHY: explains the reason behind a decision
```

---

*Continue to → [Chapter 1: Distributed Systems](./01-distributed-systems.md)*
