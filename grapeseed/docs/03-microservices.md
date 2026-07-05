# Chapter 3 — Microservices

> *"The goal of microservices is not small services. It's services with clear business boundaries that can be independently deployed."*  
> — Sam Newman, *Building Microservices*

---

## 3.1 What Is a Microservice?

A microservice is a small, independently deployable service that is responsible for exactly
one *business capability*. In GrapeSeed:

- **TenantService** owns everything about tenants and payment.
- **IdentityService** owns everything about authentication and sessions.
- **VideoService** owns everything about video storage and streaming.
- **RecommendationService** owns everything about surfacing the right video to the right student.

The word "micro" is misleading — size is not the point. A service could have 10,000 lines of
code and still be a good microservice if it encapsulates a single, well-defined capability.
Conversely, a 100-line service that is tightly coupled to every other service is a monolith
that happens to run in a separate process.

---

## 3.2 The Boundary: Bounded Contexts from DDD

The clearest way to find the right service boundaries is to use **Domain-Driven Design (DDD)**
bounded contexts. A bounded context is a region of the domain where a specific model applies
and specific terms have specific meanings.

For example, the word "Student" means different things in different contexts:

| Context | What "Student" means |
|---|---|
| IdentityService | A set of credentials (email, password hash, tenant ID) |
| VideoService | A viewer with a watch history and a preferred quality setting |
| RecommendationService | A vector of interests inferred from viewing behaviour |

If you try to build one `Student` class that satisfies all three contexts, it becomes bloated
and confusing. Instead, each service has its *own* Student model, relevant only to that service.
This is called **Autonomous Data Ownership**.

---

## 3.3 Service Communication Patterns

Services communicate in two fundamental ways:

### Synchronous (Request/Response)

One service calls another and *waits* for the answer. In GrapeSeed, the API Gateway routes
requests to individual services via HTTP. Students expect a fast response when they press Play.

```
Student Browser ──HTTP GET──► API Gateway ──HTTP GET──► VideoService
                                                              │
Student Browser ◄──200 OK──── API Gateway ◄──200 OK──────────┘
```

**Pros:** Simple mental model. Easy to debug. Immediate feedback.
**Cons:** Temporal coupling — if VideoService is down, the request fails even if the gateway is fine.

### Asynchronous (Event-Driven)

One service publishes an event and immediately continues. Other services consume the event
later, at their own pace. In GrapeSeed, registering a tenant publishes a `TenantRegistered`
event. The VideoService and IdentityService process it asynchronously.

```
TenantService ──SNS publish──► Topic: TenantRegistered
                                   │
                       ┌───────────┴───────────┐
                       ▼                       ▼
              [SQS Queue for Video]   [SQS Queue for Identity]
                       │                       │
              [VideoService consumer]  [IdentityService consumer]
              (runs a few ms later)    (runs a few ms later)
```

**Pros:** Loose coupling. Services can be down temporarily without losing events (SQS buffers them).
High throughput — TenantService doesn't wait for VideoService or IdentityService.
**Cons:** Harder to debug. You cannot simply follow a call stack. Requires distributed tracing.

---

## 3.4 The API Gateway

The API Gateway is the single entry point for all client traffic. It handles:

1. **Routing**: Maps URL paths to downstream services.
2. **Authentication**: Validates the JWT token. Downstream services trust it.
3. **Tenant injection**: Extracts the tenant claim and adds the `X-Tenant-Id` header.
4. **Rate limiting**: Prevents any single tenant from overwhelming the system.
5. **Observability**: Logs request latency and status codes for every upstream call.

```json
// ocelot.json — the routing configuration (see src/ApiGateway/ocelot.json)
{
  "Routes": [
    {
      "UpstreamPathTemplate": "/api/tenants/{everything}",
      "DownstreamPathTemplate": "/api/{everything}",
      "DownstreamHostAndPorts": [{ "Host": "tenant-service", "Port": 8080 }]
    },
    {
      "UpstreamPathTemplate": "/api/auth/{everything}",
      "DownstreamPathTemplate": "/api/{everything}",
      "DownstreamHostAndPorts": [{ "Host": "identity-service", "Port": 8080 }]
    }
  ]
}
```

---

## 3.5 Inter-Service Trust

When VideoService receives a request routed by the API Gateway, how does it know the JWT
was already validated? It trusts the `X-Tenant-Id` header — but only from internal traffic.

In production, this is enforced by:
1. **Network policy**: The VideoService's ECS task only accepts traffic from the API Gateway's
   security group, not from the internet.
2. **mTLS** (mutual TLS): In a zero-trust architecture, services present certificates to each
   other and verify identity at the network layer.

For this learning project, we simulate this with a simple internal API key header:
```
X-Internal-Key: grapeseed-internal-secret
```

---

## 3.6 Data Management in Microservices

### The Database-per-Service Rule

Each microservice must own its own data store. No service should query another service's
database directly. This is the hardest rule to follow because it feels inefficient — why not
just JOIN the tables?

The answer: because that JOIN creates a hidden coupling between the services. Now you cannot
deploy VideoService without ensuring the IdentityService's schema is compatible. You lose the
independent deployability that makes microservices valuable in the first place.

### Handling Distributed Queries

When the API Gateway needs to display a student's profile page (which includes their name from
IdentityService *and* their recent videos from VideoService), it has two options:

**Option A: API Composition (used in GrapeSeed)**
The client makes two separate API calls and composes the result in the browser/app.
Simple, but results in more round trips.

**Option B: CQRS with a Read Model**
A dedicated *read service* subscribes to events from both IdentityService and VideoService,
building a denormalized view optimised for the profile page query. More complex, but single API call.

---

## 3.7 The SharedKernel: Shared Without Coupling

GrapeSeed uses a **SharedKernel** project — a library referenced by all services. It contains:

- Base classes (`Entity<TId>`, `AggregateRoot<TId>`, `ValueObject`)
- The `Result<T>` monad for railway-oriented error handling
- `ITenantContext` and `TenantMiddleware`
- MediatR pipeline behaviours
- The `IEventPublisher` interface

Critically, the SharedKernel contains **no business logic** and **no infrastructure code**
(no EF Core, no AWS SDK). It is pure abstractions and primitives. This is the line between
"shared vocabulary" and "shared implementation" — crossing it creates exactly the coupling
microservices are designed to avoid.

---

*Continue to → [Chapter 4: AWS Services](./04-aws-services.md)*
