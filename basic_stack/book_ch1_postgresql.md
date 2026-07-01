# Chapter 1: PostgreSQL — The World's Most Advanced Open Source Database

---

## 1.1 What Is PostgreSQL and Why Should You Care?

PostgreSQL (often called "Postgres") is an **object-relational database management system (ORDBMS)**. It was born at the University of California, Berkeley in 1986 as a successor to the Ingres project. The name PostgreSQL comes from "Post-Ingres SQL." Today it is maintained by a global volunteer community called the PostgreSQL Global Development Group.

PostgreSQL is not just a relational database. It supports:
- **Relational** (tables, joins, foreign keys)
- **Document** (JSONB — binary JSON, fast and indexable)
- **Full-text search** (built-in, with dictionaries and ranking)
- **Geospatial** (PostGIS extension — used by mapping applications)
- **Time-series** (TimescaleDB extension)
- **Graph-like** (recursive CTEs, ltree extension)

This flexibility makes it a single database that can replace several specialized tools.

---

## 1.2 Architecture: How PostgreSQL Works Internally

Understanding the internals helps you write better queries and troubleshoot performance issues.

### 1.2.1 Process Model

PostgreSQL uses a **multi-process architecture** (not multi-threaded like MySQL). Each client connection spawns a dedicated **backend process**. This is safer (a crash in one process does not kill others) but less memory-efficient at very high connection counts (which is why connection poolers like PgBouncer exist).

```
Client App (your .NET app)
       │  TCP connection
       ▼
[Postmaster Process]  ← the main daemon
       │  forks
       ▼
[Backend Process 1] ← dedicated to client 1
[Backend Process 2] ← dedicated to client 2
[Backend Process N] ← dedicated to client N

Background Workers:
[WAL Writer]      ← writes transaction log to disk
[Checkpointer]    ← periodically flushes dirty pages to disk
[Autovacuum]      ← reclaims dead row space automatically
[Background Writer] ← proactively writes pages to reduce I/O spikes
[Stats Collector]   ← collects query statistics
```

### 1.2.2 Memory Architecture

PostgreSQL memory is divided into:

| Area | Description |
|---|---|
| **Shared Buffers** | The main cache for data pages. Default 128MB, recommended 25% of RAM |
| **WAL Buffers** | Buffer for Write-Ahead Log before it's flushed to disk |
| **Work Mem** | Memory per operation (sort, hash join). Default 4MB per sort node |
| **Maintenance Work Mem** | Memory for VACUUM, CREATE INDEX. Default 64MB |
| **Effective Cache Size** | Hint to the query planner about OS cache available. Default 4GB |

Tuning `shared_buffers` and `work_mem` are the two most impactful performance settings.

---

## 1.3 MVCC — Multi-Version Concurrency Control

This is the most important concept to understand about how PostgreSQL handles concurrent reads and writes.

### The Problem MVCC Solves

In a naive database, if Reader A is reading a row while Writer B is updating it, you need a lock. Reader A must wait. This kills concurrency.

### The MVCC Solution

PostgreSQL **never overwrites rows**. Instead:
- **UPDATE** = insert a new version of the row + mark the old version as deleted
- **DELETE** = mark the row as deleted (not physically removed)
- **INSERT** = write a new version

Each row version has:
- `xmin` — the transaction ID that created this row version
- `xmax` — the transaction ID that deleted/updated this row version (0 if still live)

When you read a row, PostgreSQL checks: "Is this version visible to my transaction?" Based on transaction isolation level, you see a **snapshot** of the database at a point in time.

```
-- MVCC in action: timeline for one row

Transaction 100: INSERT INTO users VALUES (1, 'Alice')
  → Row: (id=1, name='Alice', xmin=100, xmax=0)   -- live

Transaction 101: UPDATE users SET name='Alice Smith' WHERE id=1
  → Old row: (id=1, name='Alice', xmin=100, xmax=101)   -- dead to T102+
  → New row: (id=1, name='Alice Smith', xmin=101, xmax=0) -- live to T102+

-- A reader at Transaction 99 still sees 'Alice'
-- A reader at Transaction 102 sees 'Alice Smith'
-- Readers are NEVER blocked by writers!
```

### Dead Tuples and VACUUM

Because old row versions accumulate, PostgreSQL has **VACUUM** — a process that physically removes dead row versions (called "dead tuples") and reclaims space.

`AUTOVACUUM` runs automatically in the background. You can also run it manually:

```sql
VACUUM ANALYZE users;       -- Clean dead tuples + update statistics
VACUUM FULL users;          -- Full rewrite — acquires exclusive lock, use with care
```

---

## 1.4 Transactions and Isolation Levels

### ACID Properties

| Property | Meaning |
|---|---|
| **Atomicity** | Either all statements in a transaction succeed, or none do |
| **Consistency** | The database moves from one valid state to another |
| **Isolation** | Concurrent transactions appear to run sequentially |
| **Durability** | Committed data survives crashes (WAL ensures this) |

### Isolation Levels

PostgreSQL supports four isolation levels:

```sql
-- 1. READ COMMITTED (default)
-- You see committed data as of the start of each STATEMENT.
-- Most common choice. Good for OLTP workloads.
BEGIN;
SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
SELECT balance FROM accounts WHERE id = 1;  -- sees latest committed data
COMMIT;

-- 2. REPEATABLE READ
-- You see data as of the start of your TRANSACTION.
-- Same SELECT always returns same rows within the transaction.
-- Prevents "fuzzy reads" but NOT phantom reads (though Postgres actually prevents those too).
BEGIN;
SET TRANSACTION ISOLATION LEVEL REPEATABLE READ;
SELECT * FROM orders WHERE user_id = 42;  -- snapshot taken here
-- ... even if another transaction inserts orders for user 42, you won't see them
COMMIT;

-- 3. SERIALIZABLE
-- Strongest isolation. Transactions behave as if they ran one after another.
-- Prevents ALL anomalies. PostgreSQL uses SSI (Serializable Snapshot Isolation).
-- May cause serialization failures — your app must retry on error.
BEGIN;
SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
-- ... complex business logic that must be fully consistent
COMMIT;
```

### Savepoints — Partial Rollbacks

```sql
BEGIN;
  INSERT INTO orders (user_id, total) VALUES (1, 100);
  
  SAVEPOINT before_payment;
  
  UPDATE payments SET status = 'processed' WHERE order_id = 1;
  -- Oops, this fails...
  
  ROLLBACK TO SAVEPOINT before_payment;
  -- The INSERT above is still intact
  
  -- Try alternative payment logic...
COMMIT;
```

---

## 1.5 Data Types — PostgreSQL's Superpowers

PostgreSQL has an exceptionally rich type system:

```sql
-- Numeric
INTEGER, BIGINT, NUMERIC(10,2), REAL, DOUBLE PRECISION
SERIAL (auto-increment), BIGSERIAL

-- Text
CHAR(n), VARCHAR(n), TEXT  -- TEXT is preferred, no performance penalty for long strings

-- Date/Time
DATE, TIME, TIMESTAMP, TIMESTAMPTZ (with timezone — ALWAYS prefer this)
INTERVAL  -- e.g., INTERVAL '3 days 2 hours'

-- Boolean
BOOLEAN  -- TRUE, FALSE, NULL

-- Binary
BYTEA  -- stores raw binary data

-- Network types (unique to Postgres)
INET   -- stores IP addresses: '192.168.1.1'
CIDR   -- stores IP network: '192.168.1.0/24'
MACADDR  -- stores MAC address

-- Geometric types
POINT, LINE, CIRCLE, BOX, POLYGON

-- JSON types
JSON   -- stored as text, validated
JSONB  -- stored as binary, compressed, INDEXABLE — almost always prefer JSONB

-- Array types -- any type can be an array
INTEGER[], TEXT[], JSONB[]

-- UUID
UUID   -- e.g., gen_random_uuid()

-- Enumeration
CREATE TYPE order_status AS ENUM ('pending', 'processing', 'completed', 'cancelled');

-- Range types
INT4RANGE, TSTZRANGE  -- e.g., '[2024-01-01, 2024-12-31]'::TSTZRANGE
```

### JSONB in Depth

JSONB is one of PostgreSQL's most powerful features — it lets you store semi-structured data while still indexing and querying it efficiently.

```sql
CREATE TABLE products (
    id       SERIAL PRIMARY KEY,
    name     TEXT NOT NULL,
    metadata JSONB NOT NULL DEFAULT '{}'
);

INSERT INTO products (name, metadata) VALUES
  ('Laptop', '{"brand": "Dell", "specs": {"ram": 16, "storage": 512}, "tags": ["electronics", "portable"]}'),
  ('Phone',  '{"brand": "Apple", "specs": {"ram": 8, "storage": 256}, "tags": ["electronics", "mobile"]}');

-- Access operators
SELECT metadata -> 'brand' FROM products;          -- returns JSON: "Dell"
SELECT metadata ->> 'brand' FROM products;         -- returns text: Dell
SELECT metadata -> 'specs' -> 'ram' FROM products; -- nested: 16
SELECT metadata ->> 'specs' -> 'ram' FROM products; -- ERROR: ->> returns text, can't chain ->

-- Correct nested text extraction:
SELECT metadata #>> '{specs, ram}' FROM products;  -- path operator: '16'

-- Filter by JSONB value
SELECT * FROM products WHERE metadata ->> 'brand' = 'Dell';

-- Filter by nested value
SELECT * FROM products WHERE (metadata -> 'specs' ->> 'ram')::INT >= 16;

-- Check if key exists
SELECT * FROM products WHERE metadata ? 'brand';

-- Check if array contains element
SELECT * FROM products WHERE metadata -> 'tags' ? 'electronics';

-- JSONB update (merge)
UPDATE products
SET metadata = metadata || '{"color": "silver"}'::JSONB
WHERE id = 1;

-- Remove a key
UPDATE products
SET metadata = metadata - 'color'
WHERE id = 1;

-- GIN index for fast JSONB queries
CREATE INDEX idx_products_metadata ON products USING GIN(metadata);

-- After the index, the ? operator and @> operator are fast
SELECT * FROM products WHERE metadata @> '{"brand": "Dell"}'; -- "contains"
```

---

## 1.6 Indexes — Making Queries Fast

An index is a separate data structure that allows PostgreSQL to find rows without scanning every row in the table (a "sequential scan").

### 1.6.1 B-Tree Index (Default)

The most common type. A balanced tree structure. Good for:
- Equality comparisons: `WHERE email = 'alice@example.com'`
- Range queries: `WHERE price BETWEEN 10 AND 100`
- Sorting: `ORDER BY created_at DESC`
- Pattern matching from left: `WHERE name LIKE 'Ali%'`

```sql
-- Single column index
CREATE INDEX idx_users_email ON users(email);

-- Composite index (column order matters!)
CREATE INDEX idx_orders_user_status ON orders(user_id, status);
-- This satisfies: WHERE user_id = 1 AND status = 'pending'
-- This satisfies: WHERE user_id = 1  (uses first column)
-- This does NOT help: WHERE status = 'pending' (second column alone)

-- Partial index (index only matching rows)
CREATE INDEX idx_orders_pending ON orders(user_id)
WHERE status = 'pending';
-- Smaller index, only covers pending orders — very fast for "find pending orders per user"

-- Expression index
CREATE INDEX idx_users_lower_email ON users(LOWER(email));
-- Enables: WHERE LOWER(email) = LOWER('Alice@EXAMPLE.COM')

-- Unique index (also enforces uniqueness)
CREATE UNIQUE INDEX idx_users_email_unique ON users(email);
```

### 1.6.2 GIN Index (Generalized Inverted Index)

Best for: JSONB, arrays, full-text search. It indexes each **element** inside a container.

```sql
-- GIN on JSONB
CREATE INDEX idx_products_metadata_gin ON products USING GIN(metadata);

-- GIN on text array
CREATE TABLE articles (
    id    SERIAL PRIMARY KEY,
    title TEXT,
    tags  TEXT[]
);
CREATE INDEX idx_articles_tags ON articles USING GIN(tags);

SELECT * FROM articles WHERE tags @> ARRAY['postgresql', 'database'];  -- uses GIN index

-- GIN for full-text search
CREATE INDEX idx_articles_fts ON articles USING GIN(to_tsvector('english', title));
SELECT * FROM articles
WHERE to_tsvector('english', title) @@ to_tsquery('english', 'database & performance');
```

### 1.6.3 GiST Index (Generalized Search Tree)

Best for: geometric types, range types, full-text search (alternative to GIN).

```sql
-- GiST for range type — find overlapping time slots
CREATE TABLE bookings (
    id       SERIAL PRIMARY KEY,
    room_id  INT,
    during   TSTZRANGE
);
CREATE INDEX idx_bookings_during ON bookings USING GIST(during);

-- Find all bookings that overlap with a given time range
SELECT * FROM bookings
WHERE during && '[2024-06-01 09:00, 2024-06-01 11:00)'::TSTZRANGE;
```

### 1.6.4 BRIN Index (Block Range INdex)

Very small index for **naturally ordered** large tables. It only stores min/max values per block of pages.

```sql
-- Perfect for append-only time-series tables
CREATE TABLE sensor_readings (
    id          BIGSERIAL PRIMARY KEY,
    sensor_id   INT,
    recorded_at TIMESTAMPTZ,
    value       NUMERIC
);

-- BRIN is tiny but very effective for range queries on monotonically increasing columns
CREATE INDEX idx_readings_time_brin ON sensor_readings USING BRIN(recorded_at);
```

---

## 1.7 Query Planning and EXPLAIN ANALYZE

PostgreSQL's query **planner** decides HOW to execute your query. It estimates costs and picks the cheapest plan. Understanding this is critical for performance tuning.

```sql
-- EXPLAIN shows the plan (estimated, no actual execution)
EXPLAIN SELECT * FROM orders WHERE user_id = 42;

-- EXPLAIN ANALYZE actually executes the query and shows real timings
EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT)
SELECT o.id, o.total, u.email
FROM orders o
JOIN users u ON o.user_id = u.id
WHERE o.status = 'pending'
ORDER BY o.created_at DESC
LIMIT 20;
```

### Reading EXPLAIN Output

```
Limit  (cost=12.34..12.39 rows=20 width=64) (actual time=0.523..0.531 rows=20 loops=1)
  ->  Sort  (cost=12.34..14.09 rows=700 width=64) (actual time=0.521..0.524 rows=20 loops=1)
        Sort Key: orders.created_at DESC
        Sort Method: top-N heapsort  Memory: 26kB
        ->  Hash Join  (cost=5.25..8.75 rows=700 width=64) (actual time=0.234..0.412 rows=180 loops=1)
              Hash Cond: (orders.user_id = users.id)
              ->  Index Scan using idx_orders_status on orders
                    Index Cond: (status = 'pending')
              ->  Hash  (cost=3.00..3.00 rows=180 width=36)
                    Buckets: 1024  Batches: 1  Memory Usage: 17kB
                    ->  Seq Scan on users
```

| Term | Meaning |
|---|---|
| `cost=X..Y` | X = startup cost, Y = total cost (in arbitrary planner units) |
| `rows=N` | Estimated number of rows (if far from actual rows, statistics are stale — run ANALYZE) |
| `actual time=X..Y` | Real time in milliseconds |
| `Seq Scan` | Reading entire table — may indicate missing index |
| `Index Scan` | Using an index — good |
| `Index Only Scan` | All needed columns are in the index — best for read performance |
| `Hash Join` | Build hash table of smaller relation, probe with larger — good for larger sets |
| `Nested Loop` | For each row in outer, look up inner — good when inner is small and indexed |

---

## 1.8 Advanced SQL Features

### Window Functions

```sql
-- Rank orders by total per user
SELECT
    user_id,
    order_id,
    total,
    RANK() OVER (PARTITION BY user_id ORDER BY total DESC) as rank_in_user,
    ROW_NUMBER() OVER (ORDER BY created_at) as global_row_num,
    SUM(total) OVER (PARTITION BY user_id) as user_lifetime_value,
    LAG(total, 1) OVER (PARTITION BY user_id ORDER BY created_at) as previous_order_total
FROM orders;

-- Running total
SELECT
    created_at::DATE as day,
    SUM(total) as daily_revenue,
    SUM(SUM(total)) OVER (ORDER BY created_at::DATE) as cumulative_revenue
FROM orders
GROUP BY created_at::DATE
ORDER BY day;
```

### Common Table Expressions (CTEs)

```sql
-- Simple CTE
WITH active_users AS (
    SELECT id, email FROM users WHERE last_login > NOW() - INTERVAL '30 days'
),
user_orders AS (
    SELECT user_id, COUNT(*) as order_count, SUM(total) as total_spent
    FROM orders
    GROUP BY user_id
)
SELECT u.email, uo.order_count, uo.total_spent
FROM active_users u
JOIN user_orders uo ON u.id = uo.user_id
ORDER BY uo.total_spent DESC;

-- Recursive CTE — great for hierarchies (org charts, categories)
WITH RECURSIVE category_tree AS (
    -- Base case: root categories
    SELECT id, name, parent_id, 0 AS depth, name::TEXT AS path
    FROM categories
    WHERE parent_id IS NULL

    UNION ALL

    -- Recursive case: children
    SELECT c.id, c.name, c.parent_id, ct.depth + 1, ct.path || ' > ' || c.name
    FROM categories c
    JOIN category_tree ct ON c.parent_id = ct.id
)
SELECT id, depth, path FROM category_tree ORDER BY path;
```

### Full-Text Search

```sql
-- tsvector: document representation
-- tsquery: search query

SELECT to_tsvector('english', 'The quick brown fox jumps over the lazy dog');
-- Output: 'brown':3 'dog':9 'fox':4 'jump':5 'lazi':8 'quick':2

SELECT to_tsquery('english', 'quick & fox');
-- Output: 'quick' & 'fox'

-- Full-text search with ranking
SELECT
    id,
    title,
    ts_rank(to_tsvector('english', title || ' ' || body), query) AS rank
FROM articles, to_tsquery('english', 'postgresql & performance') query
WHERE to_tsvector('english', title || ' ' || body) @@ query
ORDER BY rank DESC;

-- Create a dedicated tsvector column + index for production performance
ALTER TABLE articles ADD COLUMN fts_vector TSVECTOR;

CREATE FUNCTION update_fts() RETURNS TRIGGER AS $$
BEGIN
    NEW.fts_vector := to_tsvector('english', coalesce(NEW.title,'') || ' ' || coalesce(NEW.body,''));
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER articles_fts_update
BEFORE INSERT OR UPDATE ON articles
FOR EACH ROW EXECUTE FUNCTION update_fts();

CREATE INDEX idx_articles_fts ON articles USING GIN(fts_vector);
```

---

## 1.9 Table Partitioning

Partitioning splits a large table into smaller physical pieces called partitions. PostgreSQL handles routing automatically — queries and inserts go to the right partition.

```sql
-- RANGE partitioning by date (classic for time-series data)
CREATE TABLE orders (
    id          BIGSERIAL,
    user_id     INT NOT NULL,
    total       NUMERIC(10,2),
    created_at  TIMESTAMPTZ NOT NULL
) PARTITION BY RANGE (created_at);

-- Create partitions for each quarter
CREATE TABLE orders_2024_q1 PARTITION OF orders
    FOR VALUES FROM ('2024-01-01') TO ('2024-04-01');

CREATE TABLE orders_2024_q2 PARTITION OF orders
    FOR VALUES FROM ('2024-04-01') TO ('2024-07-01');

-- Indexes on the parent table are automatically applied to partitions
CREATE INDEX ON orders (created_at, user_id);

-- Queries automatically use only relevant partitions (partition pruning)
SELECT * FROM orders WHERE created_at >= '2024-04-01' AND created_at < '2024-07-01';
-- Only orders_2024_q2 is scanned!

-- LIST partitioning by value (e.g., by country)
CREATE TABLE users_by_region (
    id      BIGSERIAL,
    email   TEXT,
    region  TEXT NOT NULL
) PARTITION BY LIST (region);

CREATE TABLE users_asia PARTITION OF users_by_region FOR VALUES IN ('VN', 'TH', 'SG', 'PH');
CREATE TABLE users_us   PARTITION OF users_by_region FOR VALUES IN ('US', 'CA');
```

---

## 1.10 Replication

### Streaming Replication (Physical)
PostgreSQL streams WAL (Write-Ahead Log) from primary to standby servers in real time. The standby is a byte-for-byte copy of the primary.

```
[Primary DB] --WAL Stream--> [Standby DB 1] (read replica, hot standby)
                         --> [Standby DB 2] (failover candidate)
```

### Logical Replication
Replicates changes at the logical level (individual row changes). Useful for:
- Replicating specific tables (not entire cluster)
- Replicating between different PostgreSQL versions
- Streaming changes to downstream consumers (Kafka via Debezium)

---

## 1.11 Connection Pooling with PgBouncer

PostgreSQL creates one OS process per connection. At 500+ connections, this consumes significant RAM and CPU context-switching. **PgBouncer** is a lightweight proxy that maintains a small pool of actual DB connections and multiplexes many application connections onto them.

```
[App Instance 1] ─┐
[App Instance 2] ─┤── [PgBouncer] ──(10-50 real connections)──> [PostgreSQL]
[App Instance 3] ─┘
[App Instance 4] ─┘
 (1000 app connections become 50 DB connections)
```

Modes:
- **Session pooling**: One DB connection per client session (safest, least pooling)
- **Transaction pooling**: DB connection assigned per transaction (most common, best performance)
- **Statement pooling**: Per statement (restrictive, rarely used)

In .NET with EF Core, set:
```csharp
// In connection string, reduce pool size since PgBouncer handles it
"Host=pgbouncer.internal;Database=mydb;Username=app;Password=secret;Maximum Pool Size=20"
```

---

## 1.12 PostgreSQL in .NET — Deep Dive

### EF Core Configuration

```csharp
// AppDbContext.cs
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Apply all configurations from assembly (cleaner than inline)
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
    }
}

// UserConfiguration.cs — IEntityTypeConfiguration
public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");

        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).HasColumnName("id").UseIdentityAlwaysColumn();

        builder.Property(u => u.Email)
            .HasColumnName("email")
            .HasMaxLength(255)
            .IsRequired();

        builder.HasIndex(u => u.Email).IsUnique();

        builder.Property(u => u.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("NOW()");

        // JSONB column mapping
        builder.Property(u => u.Metadata)
            .HasColumnName("metadata")
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions)null!),
                v => JsonSerializer.Deserialize<Dictionary<string, object>>(v, (JsonSerializerOptions)null!)!
            );

        builder.HasMany(u => u.Orders)
            .WithOne(o => o.User)
            .HasForeignKey(o => o.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
```

### Raw SQL and Dapper for Complex Queries

Sometimes EF Core generates suboptimal SQL. Use raw SQL or Dapper for complex queries:

```csharp
// Install: dotnet add package Dapper

public class OrderReportRepository
{
    private readonly string _connectionString;

    public OrderReportRepository(IConfiguration config)
        => _connectionString = config.GetConnectionString("Default")!;

    public async Task<IEnumerable<OrderReportDto>> GetMonthlyReportAsync(int year)
    {
        const string sql = @"
            WITH monthly_stats AS (
                SELECT
                    DATE_TRUNC('month', created_at) AS month,
                    COUNT(*) AS order_count,
                    SUM(total) AS revenue,
                    AVG(total) AS avg_order_value,
                    COUNT(DISTINCT user_id) AS unique_customers
                FROM orders
                WHERE EXTRACT(YEAR FROM created_at) = @Year
                  AND status = 'completed'
                GROUP BY DATE_TRUNC('month', created_at)
            )
            SELECT
                month,
                order_count AS OrderCount,
                revenue AS Revenue,
                avg_order_value AS AvgOrderValue,
                unique_customers AS UniqueCustomers,
                SUM(revenue) OVER (ORDER BY month) AS CumulativeRevenue
            FROM monthly_stats
            ORDER BY month";

        await using var connection = new NpgsqlConnection(_connectionString);
        return await connection.QueryAsync<OrderReportDto>(sql, new { Year = year });
    }
}
```

### EF Core Migrations Best Practices

```bash
# Create a migration
dotnet ef migrations add AddProductPriceIndex --project src/Infrastructure --startup-project src/Api

# Review the migration file BEFORE applying (always!)
# Then apply:
dotnet ef database update --project src/Infrastructure --startup-project src/Api

# Script migrations for production (safer than running migrations at startup)
dotnet ef migrations script --idempotent --output migrations.sql
```

```csharp
// NEVER run migrations in production at startup automatically for large apps
// Instead, run them as part of your deployment pipeline

// For smaller apps, conditional migration at startup is acceptable:
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await context.Database.MigrateAsync();
}
```

---

## 1.13 Performance Checklist

| Issue | Diagnosis | Fix |
|---|---|---|
| Slow query | `EXPLAIN ANALYZE` shows Seq Scan | Add appropriate index |
| Stale statistics | Row count estimates wildly wrong | Run `ANALYZE table_name` |
| Too many connections | `pg_stat_activity` shows 200+ connections | Add PgBouncer |
| Dead tuples accumulating | `pgstattuple` shows high dead ratio | Run `VACUUM ANALYZE` or tune autovacuum |
| Lock contention | `pg_locks` + `pg_stat_activity` | Reduce transaction duration, use `SKIP LOCKED` for queues |
| Index bloat | Index size >> table size | `REINDEX CONCURRENTLY index_name` |
| High `work_mem` needed | Sorts spilling to disk | Increase `work_mem` or rewrite query |

```sql
-- Find slowest queries (requires pg_stat_statements extension)
CREATE EXTENSION pg_stat_statements;

SELECT
    query,
    calls,
    total_exec_time / calls AS avg_ms,
    rows / calls AS avg_rows
FROM pg_stat_statements
ORDER BY total_exec_time DESC
LIMIT 20;

-- Find missing indexes (tables with many sequential scans)
SELECT
    schemaname,
    tablename,
    seq_scan,
    seq_tup_read,
    idx_scan,
    n_live_tup
FROM pg_stat_user_tables
WHERE seq_scan > idx_scan
  AND n_live_tup > 1000
ORDER BY seq_tup_read DESC;

-- Find unused indexes (wasting write overhead)
SELECT
    schemaname || '.' || tablename AS table,
    indexname,
    idx_scan AS times_used,
    pg_size_pretty(pg_relation_size(indexrelid)) AS size
FROM pg_stat_user_indexes
WHERE idx_scan = 0
  AND indexrelname NOT LIKE 'pg_%'
ORDER BY pg_relation_size(indexrelid) DESC;
```

---

## Summary

PostgreSQL is not just a database — it is a **platform**. Its combination of MVCC for high concurrency, rich data types (especially JSONB), powerful indexing options, and robust extensions make it suitable for virtually every application type. The key things to internalize are:

1. **MVCC** ensures readers never block writers — understand `xmin`/`xmax`
2. **Indexes are not magic** — wrong index types or stale statistics hurt more than no index
3. **EXPLAIN ANALYZE** is your best debugging tool
4. **JSONB** gives you document flexibility without sacrificing relational power
5. **Partitioning** is how you keep multi-billion-row tables manageable
6. **PgBouncer** is essential for high-connection-count applications
