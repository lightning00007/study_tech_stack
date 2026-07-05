# Chapter 1 — Distributed Systems

> *"A distributed system is one in which the failure of a computer you didn't even know existed can render your own computer unusable."*  
> — Leslie Lamport

---

## 1.1 What Makes a System "Distributed"?

A system becomes distributed the moment its components live on more than one machine and
communicate over a network. In GrapeSeed, when the IdentityService validates a student's
password and then asks the RecommendationService for their video list, those two services
might be running on completely different servers — perhaps even in different AWS availability
zones. The network between them is the thing that makes the system both powerful and
treacherous.

The power: you can scale each service independently, deploy them separately, and have them
written by different teams using different languages if needed.

The treachery: networks are unreliable. A message sent is not a message received. A function
call that takes microseconds locally can take milliseconds — or fail entirely — over the wire.

---

## 1.2 The Eight Fallacies of Distributed Computing

In 1994, Peter Deutsch (later joined by James Gosling) identified eight assumptions that
engineers new to distributed systems incorrectly make. Understanding these is the single most
valuable thing you can learn before writing your first networked service.

| Fallacy | The Reality |
|---|---|
| The network is reliable | Packets are dropped, connections timeout. Always design for failure. |
| Latency is zero | Network calls take time. Batch your calls; never call in a loop. |
| Bandwidth is infinite | Large payloads cost money and time. Prefer small, focused messages. |
| The network is secure | Always use TLS, validate tokens, and sanitise inputs. |
| Topology doesn't change | IPs and hostnames change. Use service discovery, not hardcoded addresses. |
| There is one administrator | Multiple teams own multiple services. Automation and contracts matter. |
| Transport cost is zero | Data transfer has a dollar cost on AWS. Monitor it. |
| The network is homogeneous | Different services, different tech stacks. Use standard protocols (HTTP, gRPC, AMQP). |

---

## 1.3 The CAP Theorem Explained

In 2000, Eric Brewer proposed that any distributed data store can guarantee at most **two** of
three properties simultaneously:

```
         C ─────── Consistency
        / \         All nodes see the same data at the same time.
       /   \
      /     \
     A ─────── P
Availability    Partition
  Every          Tolerance
 request gets   The system works
 a response     even when the network
                splits into islands.
```

In practice, partition tolerance is not optional — network splits happen. So the real question
is: when a split occurs, do you choose **Consistency** (refuse to serve stale data) or
**Availability** (serve possibly-stale data)?

**GrapeSeed's choices:**
- **PostgreSQL** (Consistency + Partition Tolerance): When the network is healthy, data is
  always consistent. If a replica is cut off, it stops serving reads.
- **Redis** (Availability + Partition Tolerance): If a Redis node is partitioned, it continues
  serving possibly-stale data. Acceptable for video recommendation caches; not for account balances.

---

## 1.4 Eventual Consistency in Practice

When the TenantService publishes a `TenantRegistered` event via SNS, it doesn't wait for
every other service to acknowledge it. The event is delivered *eventually*. During the
window between publication and consumption, the system is in an *inconsistent state*: the
VideoService doesn't yet know about the new tenant.

This is acceptable for GrapeSeed because:
1. The delay is typically under 100ms.
2. The VideoService will simply return an empty library until it processes the event.
3. The student experience is not harmed — they see "no videos yet" rather than an error.

The key insight: **eventual consistency is a trade-off, not a mistake**. You accept temporary
inconsistency in exchange for higher availability and looser coupling between services.

---

## 1.5 How GrapeSeed Handles Failures

### Retries with Exponential Backoff

When the VideoService calls MediaConvert and gets a transient error (502 Bad Gateway),
it doesn't give up immediately. It retries, but with increasing delays:

```
Attempt 1: immediate
Attempt 2: wait 1 second
Attempt 3: wait 2 seconds
Attempt 4: wait 4 seconds
Attempt 5: wait 8 seconds + random jitter
```

The random jitter prevents *thundering herd* — a scenario where thousands of services
all retry at exactly the same moment, creating a spike that crashes the server they're
retrying against.

### Dead Letter Queues (DLQ)

Every SQS queue in GrapeSeed has a companion Dead Letter Queue. If a message fails
processing five times, SQS automatically moves it to the DLQ. A CloudWatch alarm fires,
alerting the on-call engineer. The message is preserved for inspection and manual replay —
no data is lost.

```
Normal Queue ──(fails 5x)──► Dead Letter Queue ──► CloudWatch Alarm ──► PagerDuty
```

### Circuit Breaker Pattern

```
[CLOSED] ──(failures exceed threshold)──► [OPEN]
   ▲                                          │
   │                                          ▼
   └──(probe succeeds)──── [HALF-OPEN] ◄──(timeout)──┘
```

When a downstream service is unhealthy, a Circuit Breaker stops sending requests to it
temporarily, returning cached or default responses instead. This prevents cascade failures
where one slow service brings down the entire system.

In .NET, this is typically implemented with **Polly** (`AddPolicyHandler` in `HttpClientFactory`).
See the ApiGateway's `Program.cs` for the configuration pattern.

---

## 1.6 Idempotency: The Safety Net for Retries

Because we retry failed operations, we need to be careful: what happens if the first attempt
actually *succeeded*, but the success response was lost in the network? If we retry blindly,
we might charge a tenant's credit card twice.

The solution is **idempotency**: design every operation so that performing it multiple times
has the same effect as performing it once. In GrapeSeed:

- Every payment webhook from Stripe includes a unique `idempotencyKey`.
- Before processing a payment, the TenantService checks if that key has already been processed.
- If yes, it returns the cached response without charging the card again.

```csharp
// 📖 CONCEPT: Idempotency check pattern
// If we've already processed this event, return early.
if (await _outboxRepository.HasBeenProcessedAsync(command.IdempotencyKey))
{
    return Result.Success(); // Safe to return — nothing was double-charged
}
```

---

*Continue to → [Chapter 2: Multi-Tenancy](./02-multi-tenancy.md)*
