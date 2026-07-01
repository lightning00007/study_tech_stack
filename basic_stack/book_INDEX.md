# 📖 Complete Technical Learning Book — Master Index

> **10 Chapters · Deep Dive · Production-Quality Code**
> Designed to prepare you for real enterprise work.

---

## 📂 All Chapters

| # | Chapter | File |
|---|---|---|
| 1 | **PostgreSQL** — Architecture, MVCC, Indexes, JSONB, Partitioning, EF Core | [book_ch1_postgresql.md](book_ch1_postgresql.md) |
| 2 | **SQL Server** — T-SQL, Execution Plans, Temporal Tables, RLS, AlwaysOn | [book_ch2_sqlserver.md](book_ch2_sqlserver.md) |
| 3 | **Redis Cache** — All Data Types, Persistence, Cluster, Pub/Sub, Rate Limiting | [book_ch3_redis.md](book_ch3_redis.md) |
| 4 | **AWS SQS/SNS** — Queue Types, Visibility Timeout, DLQ, Fan-out, Filtering | [book_ch4_ch5_aws_messaging_storage.md](book_ch4_ch5_aws_messaging_storage.md) |
| 5 | **AWS S3/CloudFront** — Storage Classes, Multipart Upload, Presigned URLs, OAC, Signed Cookies | [book_ch4_ch5_aws_messaging_storage.md](book_ch4_ch5_aws_messaging_storage.md) |
| 6 | **AWS CloudWatch** — Structured Logging, Logs Insights, Custom Metrics, Alarms, X-Ray | [book_ch6_ch7_ch8_aws_cloudwatch_media_lambda.md](book_ch6_ch7_ch8_aws_cloudwatch_media_lambda.md) |
| 7 | **AWS Media Services** — MediaConvert, HLS/DASH, VOD Pipeline, EventBridge | [book_ch6_ch7_ch8_aws_cloudwatch_media_lambda.md](book_ch6_ch7_ch8_aws_cloudwatch_media_lambda.md) |
| 8 | **AWS Lambda** — Execution Model, Cold Starts, All Triggers, Concurrency, Best Practices | [book_ch6_ch7_ch8_aws_cloudwatch_media_lambda.md](book_ch6_ch7_ch8_aws_cloudwatch_media_lambda.md) |
| 9 | **CQRS + MediatR** — Commands, Queries, Pipeline Behaviors, Notifications, Idempotency | [book_ch9_ch10_dotnet_patterns.md](book_ch9_ch10_dotnet_patterns.md) |
| 10 | **Unit of Work + Repository** — Generic Repo, Specifications, UoW, Audit Fields, Testing | [book_ch9_ch10_dotnet_patterns.md](book_ch9_ch10_dotnet_patterns.md) |

---

## 🧠 One-Page Quick Reference

### Databases: When to Use What

```
Need relational data?         → PostgreSQL (open-source) or SQL Server (Microsoft)
Need fast repeated reads?     → Redis (cache the DB result)
Need JSON in a column?        → PostgreSQL JSONB
Need stored procedures?       → SQL Server (enterprise standard)
Need leaderboard / ranking?   → Redis Sorted Set
Need real-time counters?      → Redis INCR
Need session storage?         → Redis (Hash per session, TTL = session lifetime)
Need distributed lock?        → Redis SET NX EX (or RedLock)
Need time-series data?        → PostgreSQL with BRIN index + partitioning
```

### AWS: Which Service For What

```
Store a file?                 → S3
Serve a file fast globally?   → S3 + CloudFront
Give temporary file access?   → S3 Pre-signed URL
Decouple two services?        → SQS (point-to-point)
Fan-out one event to many?    → SNS → SQS (fan-out)
Process async jobs?           → SQS + Lambda (or EC2 BackgroundService)
Video upload and streaming?   → S3 + MediaConvert + CloudFront (HLS)
Monitor logs?                 → CloudWatch Logs + Logs Insights
Alert on a metric?            → CloudWatch Alarm + SNS notification
Run code without a server?    → Lambda
Run code on a schedule?       → Lambda + EventBridge (cron)
Trigger code when file lands? → S3 Event → Lambda
```

### .NET Patterns: Mental Models

```
How does a request flow?
  HTTP → Controller → mediator.Send(Command/Query) → Handler → Repository/DB → Response

What is a Command?
  Intent to change state. Returns minimal result. Goes through validation. Publishes events.

What is a Query?
  Read-only. Returns DTOs. Can be cached. Never modifies state.

What is a Pipeline Behavior?
  Middleware for MediatR. Runs before/after EVERY handler. Use for: logging, validation, caching, performance.

What is a Repository?
  A collection-like abstraction over the DB. Enables mocking. Hides ORM details.

What is Unit of Work?
  Coordinates multiple repositories. One SaveChangesAsync() = one DB transaction.

Why separate them?
  Testability: mock IUnitOfWork in unit tests → no DB needed.
  Replaceability: switch from EF Core to Dapper by replacing implementations, not handlers.
```

---

## 🛣️ Hands-On Practice Roadmap

### Week 1: Databases
- [ ] **Day 1**: Run PostgreSQL in Docker. Create tables. Practice EXPLAIN ANALYZE.
  ```bash
  docker run -d -p 5432:5432 -e POSTGRES_PASSWORD=secret --name pg postgres:16
  ```
- [ ] **Day 2**: Add EF Core + Npgsql to a .NET project. Migrations. Repository pattern.
- [ ] **Day 3**: Run Redis in Docker. Practice all 6 data types in redis-cli.
  ```bash
  docker run -d -p 6379:6379 --name redis redis:7
  redis-cli   # then try: SET, GET, HSET, ZADD, LPUSH, SADD
  ```
- [ ] **Day 4**: Implement Cache-Aside pattern in your .NET project. Test cache hit vs miss.
- [ ] **Day 5**: Install SQL Server (Docker or LocalDB). Practice T-SQL, stored procedures, execution plans.

### Week 2: AWS
- [ ] **Day 1**: Create an S3 bucket. Upload a file. Try presigned URL.
- [ ] **Day 2**: Create a CloudFront distribution pointing to S3. Test CDN delivery.
- [ ] **Day 3**: Create SQS queue. Create SNS topic. Subscribe queue to topic. Publish a message.
- [ ] **Day 4**: Write a .NET Lambda function (SQS trigger). Deploy with `dotnet lambda deploy-function`.
- [ ] **Day 5**: Set up CloudWatch alarms. Write a Logs Insights query against Lambda logs.

### Week 3: .NET Patterns
- [ ] **Day 1**: Set up MediatR in a new ASP.NET Core project.
- [ ] **Day 2**: Create 2 Commands + Handlers + Validators (FluentValidation).
- [ ] **Day 3**: Create 3 Queries + Handlers. Add Redis caching to one query.
- [ ] **Day 4**: Add LoggingBehavior, ValidationBehavior, PerformanceBehavior as pipeline behaviors.
- [ ] **Day 5**: Implement Unit of Work + Repository. Write unit tests with Moq.

### Week 4: Integration
- [ ] Build a mini order system using ALL the patterns together:
  - ASP.NET Core API (controllers → MediatR)
  - CQRS handlers with Unit of Work + Repository
  - PostgreSQL as primary DB
  - Redis for caching query results
  - SQS for async order processing events
  - S3 for storing order receipts (PDF)
  - CloudWatch for logs and metrics
  - Lambda triggered by SQS to send confirmation emails

---

## 🔑 Key Concepts to Memorize

### PostgreSQL
| Concept | Remember |
|---|---|
| MVCC | Never overwrites rows. Readers never block writers. |
| VACUUM | Reclaims dead tuple space. Autovacuum does this automatically. |
| B-Tree Index | Default. Equality, range, ordering. |
| GIN Index | For JSONB, arrays, full-text search (contains element). |
| EXPLAIN ANALYZE | Run this on every slow query. Look for Seq Scan. |
| AsNoTracking() | Always use for read-only queries in EF Core. |

### SQL Server
| Concept | Remember |
|---|---|
| Clustered Index | The table IS the index. One per table. |
| Key Lookup | Warning sign — add INCLUDE columns to your index. |
| RCSI | Enable on all OLTP databases. Eliminates read/write blocking. |
| SET NOCOUNT ON | Always in stored procedures. |
| Temporal Tables | Free audit history. FOR SYSTEM_TIME AS OF gives time travel. |

### Redis
| Concept | Remember |
|---|---|
| String | Simple cache. Use INCR for counters. |
| Hash | Object storage. Field-level updates without serialize/deserialize. |
| Sorted Set | Leaderboard + sliding window rate limiter. |
| List | Queue (LPUSH + RPOP) or Stack (RPUSH + RPOP). |
| Set | Unique membership. Intersection/Union/Difference. |
| allkeys-lru | Best eviction policy for a pure cache. |
| Always set TTL | Never let cache keys live forever. |

### AWS SQS/SNS
| Concept | Remember |
|---|---|
| Visibility Timeout | The retry mechanism. Don't delete on failure. |
| Long Polling | WaitTimeSeconds=20. Always in production. |
| DLQ | Configure on every queue. Never lose a failed message. |
| FIFO Queue | When order matters (payments, state machines). |
| SNS Fan-out | One event → many independent services. |
| Message Filtering | Subscribers only get messages they care about. |

### AWS Lambda
| Concept | Remember |
|---|---|
| Cold Start | .NET has 500-2000ms cold start. Use SnapStart or Provisioned Concurrency. |
| Initialize outside handler | HttpClient, AWS clients — created once, reused. |
| 15-min limit | Lambda triggers work (MediaConvert). Doesn't run long jobs. |
| Reserved Concurrency | Protects DB from Lambda stampede. |
| Partial Batch Failure | Return failed MessageIds. Others are auto-deleted. |

### CQRS + MediatR
| Concept | Remember |
|---|---|
| Command | Writes. Validates. Publishes events. Returns ID/status. |
| Query | Reads. Returns DTOs. May use cache. Never writes. |
| Handler | One handler per request. Single responsibility. |
| Pipeline Behavior | Middleware for MediatR: logging, validation, caching. |
| Notification | Broadcast to many handlers. For domain events. |
| Idempotency | Commands from queues can arrive twice. Use IdempotencyKey. |

### Unit of Work + Repository
| Concept | Remember |
|---|---|
| Repository | Hides ORM. Enables mocking. Add domain-specific methods. |
| Specification | Encapsulates query logic. Avoids method explosion. |
| Unit of Work | Coordinates repos. One SaveChanges = one transaction. |
| Audit Fields | Set CreatedAt/UpdatedAt automatically in SaveChanges override. |
| Testing | Mock IUnitOfWork → no DB needed for unit tests. |
