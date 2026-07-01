# Chapter 2: SQL Server — Microsoft's Enterprise Database

---

## 2.1 What Is SQL Server?

Microsoft SQL Server is an enterprise **Relational Database Management System (RDBMS)** built and maintained by Microsoft. First released in 1989, it is a cornerstone of Microsoft's data platform and integrates deeply with the .NET ecosystem, Azure, and Windows Server environments.

SQL Server editions:
| Edition | Purpose |
|---|---|
| **Express** | Free, 10GB limit, for small apps and learning |
| **Developer** | Free, full features, for development only |
| **Standard** | Mid-range, limited cores/memory |
| **Enterprise** | Full features, unlimited scale |
| **Azure SQL Database** | Fully managed cloud version (PaaS) |
| **Azure SQL Managed Instance** | Near-100% compatibility with on-prem SQL Server, fully managed |

---

## 2.2 Architecture: How SQL Server Works Internally

### 2.2.1 Process Model

Unlike PostgreSQL's multi-process model, SQL Server uses a **single-process, multi-threaded** model. A single `sqlservr.exe` process manages all connections through an internal scheduler called **SQLOS (SQL Server Operating System)**.

```
sqlservr.exe (single process)
├── Connection Pool Manager
├── Buffer Pool (data cache + plan cache)
├── Log Manager (transaction log)
├── Lock Manager
├── Query Executor
├── Storage Engine
└── Worker Threads (handles queries concurrently)
```

### 2.2.2 Memory Architecture

SQL Server uses a large, dynamic **Buffer Pool** — it grabs as much RAM as it can (configurable with `max server memory`). The buffer pool serves as:
- **Data cache**: Recently accessed data pages stay in memory
- **Plan cache**: Compiled query execution plans are cached and reused

```sql
-- Check current memory usage
SELECT
    physical_memory_in_use_kb / 1024 AS used_memory_mb,
    page_fault_count
FROM sys.dm_os_process_memory;

-- Check buffer pool distribution
SELECT
    DB_NAME(database_id) AS database_name,
    COUNT(*) * 8 / 1024 AS cached_mb
FROM sys.dm_os_buffer_descriptors
GROUP BY database_id
ORDER BY cached_mb DESC;
```

### 2.2.3 Storage: Pages and Extents

SQL Server stores data in **8KB pages**. Pages are grouped into **extents** (8 pages = 64KB).

Page types:
- **Data pages**: Store actual table rows
- **Index pages**: Store index entries
- **LOB pages**: Store large objects (TEXT, IMAGE, VARBINARY(MAX))
- **IAM pages**: Index Allocation Maps — track which extents belong to each object

---

## 2.3 T-SQL Deep Dive

T-SQL (Transact-SQL) is SQL Server's extension of SQL. It adds procedural programming constructs.

### 2.3.1 Variables and Control Flow

```sql
-- Variables
DECLARE @userId INT = 42;
DECLARE @orderCount INT;
DECLARE @message NVARCHAR(500);

-- SELECT into variable
SELECT @orderCount = COUNT(*)
FROM Orders
WHERE UserId = @userId;

-- IF/ELSE
IF @orderCount > 10
BEGIN
    SET @message = 'High-value customer';
END
ELSE IF @orderCount > 0
BEGIN
    SET @message = 'Active customer';
END
ELSE
BEGIN
    SET @message = 'New customer';
END

PRINT @message;

-- WHILE loop
DECLARE @counter INT = 1;
WHILE @counter <= 5
BEGIN
    PRINT 'Iteration: ' + CAST(@counter AS NVARCHAR);
    SET @counter = @counter + 1;
END

-- CASE expression (like a switch statement, usable in SELECT)
SELECT
    OrderId,
    Total,
    CASE
        WHEN Total >= 1000 THEN 'Large'
        WHEN Total >= 100  THEN 'Medium'
        ELSE 'Small'
    END AS OrderCategory
FROM Orders;
```

### 2.3.2 Stored Procedures — The Enterprise Standard

In enterprise SQL Server environments, you will frequently encounter stored procedures. They are pre-compiled, named blocks of T-SQL that live in the database.

**Benefits:**
- **Reduced network traffic**: Send procedure name + params instead of full SQL
- **Plan caching**: Execution plan compiled once, reused
- **Security**: Grant EXECUTE permission on procedure without exposing tables
- **Encapsulation**: Business logic can live in the DB layer

```sql
-- CREATE a stored procedure
CREATE PROCEDURE dbo.usp_GetOrdersByUser
    @UserId     INT,
    @StatusFilter NVARCHAR(50) = NULL,       -- Optional parameter
    @PageNumber   INT = 1,
    @PageSize     INT = 20,
    @TotalCount   INT OUTPUT                 -- Output parameter
AS
BEGIN
    SET NOCOUNT ON;  -- Suppress "N rows affected" messages (performance)

    -- Validate input
    IF @UserId <= 0
    BEGIN
        RAISERROR('UserId must be a positive integer', 16, 1);
        RETURN;
    END;

    -- Build result
    SELECT @TotalCount = COUNT(*)
    FROM dbo.Orders
    WHERE UserId = @UserId
      AND (@StatusFilter IS NULL OR Status = @StatusFilter);

    SELECT
        o.Id,
        o.Total,
        o.Status,
        o.CreatedAt,
        u.Email
    FROM dbo.Orders o
    INNER JOIN dbo.Users u ON o.UserId = u.Id
    WHERE o.UserId = @UserId
      AND (@StatusFilter IS NULL OR o.Status = @StatusFilter)
    ORDER BY o.CreatedAt DESC
    OFFSET (@PageNumber - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY;
END;
GO

-- EXECUTE the procedure
DECLARE @total INT;
EXEC dbo.usp_GetOrdersByUser
    @UserId = 42,
    @StatusFilter = 'pending',
    @PageNumber = 1,
    @PageSize = 10,
    @TotalCount = @total OUTPUT;

PRINT 'Total orders: ' + CAST(@total AS NVARCHAR);

-- ALTER (modify)
ALTER PROCEDURE dbo.usp_GetOrdersByUser ... 

-- DROP
DROP PROCEDURE IF EXISTS dbo.usp_GetOrdersByUser;
```

### 2.3.3 Error Handling with TRY/CATCH

```sql
CREATE PROCEDURE dbo.usp_TransferFunds
    @FromAccountId  INT,
    @ToAccountId    INT,
    @Amount         DECIMAL(18,2)
AS
BEGIN
    SET NOCOUNT ON;

    BEGIN TRY
        BEGIN TRANSACTION;

            -- Deduct from source
            UPDATE dbo.Accounts
            SET Balance = Balance - @Amount
            WHERE Id = @FromAccountId AND Balance >= @Amount;

            IF @@ROWCOUNT = 0
                THROW 50001, 'Insufficient funds or account not found.', 1;

            -- Add to destination
            UPDATE dbo.Accounts
            SET Balance = Balance + @Amount
            WHERE Id = @ToAccountId;

            IF @@ROWCOUNT = 0
                THROW 50002, 'Destination account not found.', 1;

            -- Log the transfer
            INSERT INTO dbo.TransferLog (FromId, ToId, Amount, TransferredAt)
            VALUES (@FromAccountId, @ToAccountId, @Amount, GETUTCDATE());

        COMMIT TRANSACTION;

    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;

        -- Re-throw the error to the caller
        DECLARE @ErrorMessage NVARCHAR(4000) = ERROR_MESSAGE();
        DECLARE @ErrorSeverity INT = ERROR_SEVERITY();
        DECLARE @ErrorState INT = ERROR_STATE();

        RAISERROR(@ErrorMessage, @ErrorSeverity, @ErrorState);
    END CATCH;
END;
```

### 2.3.4 CTEs, Window Functions, APPLY

```sql
-- Recursive CTE: Employee hierarchy
WITH EmployeeHierarchy AS (
    -- Anchor: top-level managers (no manager)
    SELECT Id, Name, ManagerId, 0 AS Level, CAST(Name AS NVARCHAR(MAX)) AS Path
    FROM dbo.Employees WHERE ManagerId IS NULL

    UNION ALL

    -- Recursive: employees with a manager
    SELECT e.Id, e.Name, e.ManagerId, eh.Level + 1, eh.Path + ' > ' + e.Name
    FROM dbo.Employees e
    INNER JOIN EmployeeHierarchy eh ON e.ManagerId = eh.Id
)
SELECT Id, Name, Level, Path FROM EmployeeHierarchy ORDER BY Path;

-- Window Functions
SELECT
    DepartmentId,
    EmployeeName,
    Salary,
    AVG(Salary) OVER (PARTITION BY DepartmentId) AS DeptAvgSalary,
    Salary - AVG(Salary) OVER (PARTITION BY DepartmentId) AS DiffFromAvg,
    RANK() OVER (PARTITION BY DepartmentId ORDER BY Salary DESC) AS SalaryRank,
    NTILE(4) OVER (ORDER BY Salary) AS SalaryQuartile
FROM dbo.Employees;

-- CROSS APPLY (like a JOIN but calls a table-valued function per row)
SELECT u.Id, u.Email, recent.*
FROM dbo.Users u
CROSS APPLY (
    SELECT TOP 3 o.Id, o.Total, o.CreatedAt
    FROM dbo.Orders o
    WHERE o.UserId = u.Id
    ORDER BY o.CreatedAt DESC
) AS recent;
-- For each user, get their 3 most recent orders — CROSS APPLY makes this elegant
```

---

## 2.4 Indexing in SQL Server

### 2.4.1 Clustered vs Non-Clustered Indexes

**This is the most important indexing concept in SQL Server.**

| | Clustered Index | Non-Clustered Index |
|---|---|---|
| **What it is** | The physical ordering of table data on disk | A separate structure that points to the clustered key |
| **How many per table** | **One only** (the table IS the index) | Up to 999 per table |
| **Default** | Created automatically for PRIMARY KEY | Created with `CREATE INDEX` |
| **Row data** | Stored at the leaf level | Only index columns + pointer to clustered key |

```sql
-- Table with a clustered index on Id (default behavior)
CREATE TABLE dbo.Orders (
    Id          INT IDENTITY(1,1) PRIMARY KEY,   -- This IS the clustered index
    UserId      INT NOT NULL,
    Status      NVARCHAR(50) NOT NULL,
    Total       DECIMAL(18,2),
    CreatedAt   DATETIME2 NOT NULL DEFAULT GETUTCDATE()
);

-- Non-clustered index to speed up WHERE UserId = X
CREATE NONCLUSTERED INDEX IX_Orders_UserId ON dbo.Orders(UserId);

-- Non-clustered index with INCLUDED columns (avoids a Key Lookup)
-- Without INCLUDE: SQL Server finds rows via IX_Orders_UserId, then goes back to
--                  the clustered index to fetch Total and Status (expensive "Key Lookup")
-- With INCLUDE: Total and Status are stored directly in the index — no Key Lookup needed
CREATE NONCLUSTERED INDEX IX_Orders_UserId_Covering
ON dbo.Orders(UserId, CreatedAt DESC)
INCLUDE (Status, Total);

-- Filtered index (partial — only index active orders)
CREATE NONCLUSTERED INDEX IX_Orders_Active
ON dbo.Orders(UserId, CreatedAt)
WHERE Status = 'pending';
```

### 2.4.2 Columnstore Indexes

A revolutionary index type optimized for analytical (OLAP) queries. Instead of storing data row by row (row-store), it stores column by column. This enables:
- **10-100x compression** (same values in a column compress well)
- **Vectorized query execution** (process batches of column values at once)
- **Great for aggregations** (SUM, AVG, COUNT on millions of rows)

```sql
-- Clustered Columnstore Index (transforms the entire table into columnar format)
-- Use this for fact tables in data warehouses
CREATE CLUSTERED COLUMNSTORE INDEX CCI_SalesData ON dbo.SalesData;

-- Non-clustered Columnstore Index (hybrid: keep row-store for OLTP, add columnstore for analytics)
CREATE NONCLUSTERED COLUMNSTORE INDEX NCCI_Orders_Analytics
ON dbo.Orders (UserId, Status, Total, CreatedAt);

-- A query like this becomes dramatically faster with a columnstore index:
SELECT
    YEAR(CreatedAt) AS Year,
    MONTH(CreatedAt) AS Month,
    COUNT(*) AS OrderCount,
    SUM(Total) AS Revenue
FROM dbo.Orders
GROUP BY YEAR(CreatedAt), MONTH(CreatedAt)
ORDER BY Year, Month;
```

### 2.4.3 Execution Plans — SQL Server's EXPLAIN

```sql
-- Show estimated plan (no execution) — Ctrl+L in SSMS
SET SHOWPLAN_ALL ON;

-- Show actual plan (executes query) — Ctrl+M in SSMS
-- In T-SQL:
SET STATISTICS IO ON;  -- shows logical reads (most important metric)
SET STATISTICS TIME ON; -- shows CPU and elapsed time

SELECT * FROM dbo.Orders WHERE UserId = 42;
-- Table 'Orders'. Scan count 1, logical reads 3 (good, using index)
-- vs. logical reads 50000 (bad, sequential scan)
```

**Key execution plan operators to recognize:**

| Operator | Meaning |
|---|---|
| **Clustered Index Seek** | Best: found rows via clustered index efficiently |
| **Index Seek** | Good: found rows via non-clustered index |
| **Key Lookup** | Warning: fetched extra columns from clustered index after non-clustered seek — add INCLUDE columns |
| **Clustered Index Scan** | Warning: scanned entire table — missing index |
| **Sort** | Can be expensive — try to have indexes match ORDER BY |
| **Hash Match** | Join method for larger datasets |
| **Nested Loops** | Join method for small inner sets |
| **Spill to tempdb** | Critical: not enough memory for sort/hash — increase memory or tune query |

---

## 2.5 Advanced SQL Server Features

### 2.5.1 Temporal Tables (System-Versioned)

Temporal tables automatically track the full history of row changes. SQL Server maintains a history table silently.

```sql
-- Create a temporal table
CREATE TABLE dbo.Products (
    Id          INT IDENTITY(1,1) PRIMARY KEY,
    Name        NVARCHAR(200) NOT NULL,
    Price       DECIMAL(18,2) NOT NULL,
    -- System-period columns (SQL Server manages these automatically)
    ValidFrom   DATETIME2 GENERATED ALWAYS AS ROW START NOT NULL,
    ValidTo     DATETIME2 GENERATED ALWAYS AS ROW END NOT NULL,
    PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.ProductsHistory));

-- Normal DML works as usual
INSERT INTO dbo.Products (Name, Price) VALUES ('Laptop', 999.99);
UPDATE dbo.Products SET Price = 1099.99 WHERE Id = 1;
UPDATE dbo.Products SET Price = 1199.99 WHERE Id = 1;

-- Query current data (normal query)
SELECT * FROM dbo.Products WHERE Id = 1;

-- Query as of a specific point in time (time travel!)
SELECT * FROM dbo.Products
FOR SYSTEM_TIME AS OF '2024-01-15 10:30:00'
WHERE Id = 1;

-- Query all history for a row
SELECT * FROM dbo.Products
FOR SYSTEM_TIME ALL
WHERE Id = 1
ORDER BY ValidFrom;

-- Find what the price was at any point in the past — perfect for auditing!
```

### 2.5.2 Row-Level Security (RLS)

RLS lets you filter rows automatically based on who is running the query — without changing application queries.

```sql
-- Create a security policy
CREATE SCHEMA Security;
GO

-- Predicate function: returns 1 if the current user should see the row
CREATE FUNCTION Security.fn_TenantFilter(@TenantId INT)
RETURNS TABLE
WITH SCHEMABINDING
AS
RETURN SELECT 1 AS result
WHERE @TenantId = CAST(SESSION_CONTEXT(N'TenantId') AS INT);

-- Apply the policy to the Orders table
CREATE SECURITY POLICY TenantIsolationPolicy
ADD FILTER PREDICATE Security.fn_TenantFilter(TenantId) ON dbo.Orders,
ADD BLOCK  PREDICATE Security.fn_TenantFilter(TenantId) ON dbo.Orders
WITH (STATE = ON);

-- In your application, set the context before queries:
-- EXEC sp_set_session_context N'TenantId', 5;
-- Now: SELECT * FROM Orders -- automatically only returns TenantId = 5 rows
```

### 2.5.3 Transparent Data Encryption (TDE)

TDE encrypts the entire database at rest — database files, log files, and backups are encrypted. Completely transparent to applications.

```sql
-- Enable TDE
USE master;
CREATE MASTER KEY ENCRYPTION BY PASSWORD = 'StrongPass!123';
CREATE CERTIFICATE MyServerCert WITH SUBJECT = 'MyDatabase Encryption Cert';

USE MyDatabase;
CREATE DATABASE ENCRYPTION KEY
WITH ALGORITHM = AES_256
ENCRYPTION BY SERVER CERTIFICATE MyServerCert;

ALTER DATABASE MyDatabase SET ENCRYPTION ON;
```

### 2.5.4 In-Memory OLTP (Hekaton)

Memory-optimized tables store all data in RAM with lock-free algorithms. Dramatic performance improvement for high-throughput OLTP workloads.

```sql
-- Create a memory-optimized filegroup first (one-time setup)
ALTER DATABASE MyDB ADD FILEGROUP MyDB_MemoryOptimized CONTAINS MEMORY_OPTIMIZED_DATA;
ALTER DATABASE MyDB ADD FILE (NAME='MyDB_MemOpt', FILENAME='C:\Data\MyDB_MemOpt')
    TO FILEGROUP MyDB_MemoryOptimized;

-- Create memory-optimized table
CREATE TABLE dbo.SessionTokens (
    Token       NVARCHAR(100) NOT NULL CONSTRAINT PK_SessionTokens PRIMARY KEY NONCLUSTERED,
    UserId      INT NOT NULL,
    ExpiresAt   DATETIME2 NOT NULL,
    INDEX IX_User HASH (UserId) WITH (BUCKET_COUNT = 1024)  -- Hash index, unique to In-Memory OLTP
)
WITH (MEMORY_OPTIMIZED = ON, DURABILITY = SCHEMA_AND_DATA);

-- Natively compiled stored procedure (compiled to native C code, not interpreted T-SQL)
CREATE PROCEDURE dbo.usp_ValidateSession
    @Token NVARCHAR(100)
WITH NATIVE_COMPILATION, SCHEMABINDING, EXECUTE AS OWNER
AS
BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
    SELECT UserId FROM dbo.SessionTokens
    WHERE Token = @Token AND ExpiresAt > GETUTCDATE();
END;
```

---

## 2.6 Concurrency and Locking

### Lock Types

| Lock | What it covers | Who can acquire it concurrently |
|---|---|---|
| **S (Shared)** | Read operation | Multiple S locks can coexist |
| **X (Exclusive)** | Write operation | Nobody else |
| **U (Update)** | Intent to update | One U + multiple S |
| **IS, IX, SIX** | Intent locks (table level) | Coordination signal |

### Isolation Levels in SQL Server

SQL Server supports both **pessimistic** (lock-based) and **optimistic** (version-based) isolation:

```sql
-- READ COMMITTED (default) — pessimistic, readers block writers and vice versa
SET TRANSACTION ISOLATION LEVEL READ COMMITTED;

-- READ COMMITTED SNAPSHOT ISOLATION (RCSI) — optimistic, readers never block writers
-- Enable at database level:
ALTER DATABASE MyDB SET READ_COMMITTED_SNAPSHOT ON;
-- Now READ COMMITTED uses row versioning (stored in tempdb) instead of locks
-- This is HIGHLY recommended for OLTP applications — dramatically reduces blocking

-- SNAPSHOT — full snapshot isolation
ALTER DATABASE MyDB SET ALLOW_SNAPSHOT_ISOLATION ON;
SET TRANSACTION ISOLATION LEVEL SNAPSHOT;

-- Finding blocking queries
SELECT
    blocking.session_id AS blocking_session,
    blocking.command AS blocking_command,
    blocked.session_id AS blocked_session,
    blocked.wait_type,
    blocked.wait_time / 1000.0 AS wait_seconds,
    blocked_text.text AS blocked_query
FROM sys.dm_exec_requests blocked
INNER JOIN sys.dm_exec_sessions blocking ON blocked.blocking_session_id = blocking.session_id
CROSS APPLY sys.dm_exec_sql_text(blocked.sql_handle) AS blocked_text
WHERE blocked.blocking_session_id > 0;
```

---

## 2.7 SQL Server Always On Availability Groups

Always On is SQL Server's enterprise HA/DR solution. It provides:
- **Automatic failover**: Primary fails → secondary becomes primary automatically
- **Read scale-out**: Route read queries to secondary replicas
- **Zero data loss** (synchronous mode) or **minimal data loss** (asynchronous for disaster recovery)

```
[Primary Replica]  ←→  [Secondary 1 - Synchronous]  (automatic failover)
                   ←→  [Secondary 2 - Asynchronous]  (disaster recovery site)
                   ←→  [Secondary 3 - Readable only]  (reporting queries)
```

In .NET connection strings:
```csharp
// Use the Availability Group Listener (virtual name, routes to primary)
"Server=myAGListener,1433;Database=MyDB;Integrated Security=True;MultiSubnetFailover=True"

// For read-only queries, use ApplicationIntent=ReadOnly — routes to readable secondary
"Server=myAGListener,1433;Database=MyDB;Integrated Security=True;ApplicationIntent=ReadOnly"
```

---

## 2.8 SQL Server in .NET — Deep Dive

### EF Core Configuration

```csharp
// Program.cs
builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("SqlServer"),
        sqlOptions =>
        {
            // Retry transient failures (deadlocks, timeouts, network blips)
            sqlOptions.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(30),
                errorNumbersToAdd: null
            );
            // Command timeout
            sqlOptions.CommandTimeout(30);
        }
    );

    // Log generated SQL in development
    if (builder.Environment.IsDevelopment())
        options.LogTo(Console.WriteLine, LogLevel.Information)
               .EnableSensitiveDataLogging();
});
```

### Calling Stored Procedures from EF Core

```csharp
// Execute a stored procedure that returns a result set
public async Task<List<OrderSummaryDto>> GetUserOrdersAsync(int userId, string? status)
{
    var userIdParam = new SqlParameter("@UserId", userId);
    var statusParam = new SqlParameter("@StatusFilter", (object?)status ?? DBNull.Value);

    return await _context.Database
        .SqlQuery<OrderSummaryDto>(
            $"EXEC dbo.usp_GetOrdersByUser @UserId, @StatusFilter",
            userIdParam, statusParam
        )
        .ToListAsync();
}

// Execute a stored procedure with output parameter
public async Task<(List<OrderDto> Orders, int TotalCount)> GetPagedOrdersAsync(int userId, int page, int size)
{
    var userIdParam  = new SqlParameter("@UserId", userId);
    var pageParam    = new SqlParameter("@PageNumber", page);
    var sizeParam    = new SqlParameter("@PageSize", size);
    var totalParam   = new SqlParameter("@TotalCount", SqlDbType.Int) { Direction = ParameterDirection.Output };

    var orders = await _context.Database
        .SqlQuery<OrderDto>(
            $"EXEC dbo.usp_GetOrdersByUser @UserId, NULL, @PageNumber, @PageSize, @TotalCount OUTPUT",
            userIdParam, pageParam, sizeParam, totalParam
        )
        .ToListAsync();

    var totalCount = (int)totalParam.Value;
    return (orders, totalCount);
}

// Execute non-query stored procedures
public async Task TransferFundsAsync(int fromId, int toId, decimal amount)
{
    await _context.Database.ExecuteSqlRawAsync(
        "EXEC dbo.usp_TransferFunds @FromAccountId, @ToAccountId, @Amount",
        new SqlParameter("@FromAccountId", fromId),
        new SqlParameter("@ToAccountId", toId),
        new SqlParameter("@Amount", amount)
    );
}
```

### SQL Server Connection String Options

```
Server=myserver.database.windows.net,1433;
Database=MyDatabase;
User Id=myuser;
Password=MyPassword123!;
Encrypt=True;                    -- Always encrypt (required for Azure SQL)
TrustServerCertificate=False;    -- Validate server certificate
Connection Timeout=30;
ConnectRetryCount=3;
ConnectRetryInterval=10;
Application Name=MyApp;          -- Shown in sys.dm_exec_sessions for debugging
MultipleActiveResultSets=True;   -- Allows multiple simultaneous queries on one connection (required by EF Core)
```

---

## 2.9 SQL Server Differences for Postgres Developers

If you're coming from PostgreSQL, these are the key syntax differences:

| Feature | PostgreSQL | SQL Server (T-SQL) |
|---|---|---|
| Auto-increment | `SERIAL` or `GENERATED ALWAYS AS IDENTITY` | `IDENTITY(1,1)` |
| String concatenation | `\|\|` | `+` |
| Current timestamp | `NOW()` or `CURRENT_TIMESTAMP` | `GETUTCDATE()` or `SYSDATETIMEOFFSET()` |
| String limit | `TEXT` (unlimited) | `NVARCHAR(MAX)` or `VARCHAR(MAX)` |
| UPSERT | `INSERT ... ON CONFLICT DO UPDATE` | `MERGE` or `INSERT ... WHERE NOT EXISTS` |
| Top N rows | `LIMIT 10` | `TOP 10` or `FETCH NEXT 10 ROWS ONLY` |
| String functions | `SUBSTRING`, `LENGTH` | `SUBSTRING`, `LEN` |
| Boolean | `TRUE/FALSE` | `1/0` or `BIT` type |
| Schema default | `public` | `dbo` |
| Case sensitivity | Case-insensitive names by default | Case-insensitive names by default |

```sql
-- UPSERT in SQL Server (MERGE statement)
MERGE dbo.UserProfiles AS target
USING (SELECT @UserId AS UserId, @DisplayName AS DisplayName) AS source
ON target.UserId = source.UserId
WHEN MATCHED THEN
    UPDATE SET DisplayName = source.DisplayName, UpdatedAt = GETUTCDATE()
WHEN NOT MATCHED THEN
    INSERT (UserId, DisplayName, CreatedAt)
    VALUES (source.UserId, source.DisplayName, GETUTCDATE());
```

---

## Summary

SQL Server is an industrial-strength database with deep integration into the Microsoft ecosystem. Key things to internalize:

1. **Clustered vs non-clustered indexes** — the most important storage concept
2. **Covered indexes with INCLUDE** — eliminate Key Lookups for dramatic query speedup
3. **SET NOCOUNT ON** — always in stored procedures
4. **RCSI (READ_COMMITTED_SNAPSHOT)** — enable this for all OLTP databases to eliminate read/write blocking
5. **TRY/CATCH with transactions** — the standard error handling pattern
6. **Temporal tables** — free audit trail for regulatory requirements
7. **Execution plans + Logical Reads** — the two primary performance debugging tools
