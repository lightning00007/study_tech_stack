# 📖 Project 1 — The Monolith
### *"Get it working. Nothing else."*

> *"Make it work, make it right, make it fast — in that order."*  
> — Kent Beck

---

## Before You Read the Code

Before diving into syntax, understand the problem we are solving. Imagine you are asked to build a simple API that a local library uses to catalogue its books and authors. They want to:

1. See a list of all authors
2. Add a new author
3. See all books
4. Add a new book (linked to an existing author)

That is four HTTP endpoints. In this first project, we write the simplest possible code that makes those four things work. No patterns, no frameworks beyond what ASP.NET Core and EF Core provide, no architecture — just code.

---

## Chapter 1 — What Is EF Core and Why Do We Need It?

Your application lives in **RAM** — variables, objects, lists. Your database lives on **disk** (PostgreSQL in our case). These are fundamentally different environments, and bridging them is surprisingly hard.

Without EF Core, you would write raw SQL:

```csharp
// 😰 Without EF Core — writing SQL by hand
using var conn = new NpgsqlConnection(connectionString);
await conn.OpenAsync();

using var cmd = new NpgsqlCommand("SELECT id, title, isbn FROM books WHERE id = @id", conn);
cmd.Parameters.AddWithValue("@id", bookId);

using var reader = await cmd.ExecuteReaderAsync();
if (await reader.ReadAsync())
{
    var book = new Book
    {
        Id    = reader.GetInt32(0),
        Title = reader.GetString(1),
        Isbn  = reader.GetString(2)
    };
}
```

This works, but every query requires manually mapping column names to properties. You are responsible for opening and closing connections. A typo in a column name is a runtime error, not a compile-time error.

**EF Core** solves this with an **ORM (Object-Relational Mapper)**. It translates between the relational world (tables, rows, columns) and the object world (classes, objects, properties):

```csharp
// 😌 With EF Core — pure C#
var book = await db.Books.FindAsync(bookId);
```

EF Core generates the SQL, manages connections, and maps the result into a `Book` object automatically. You write C#. EF Core handles the SQL.

### What EF Core Does Under the Hood

When you write:

```csharp
var books = await db.Books
    .Include(b => b.Author)
    .Where(b => b.PublishedYear > 2000)
    .ToListAsync();
```

EF Core translates this to:

```sql
SELECT b.id, b.title, b.isbn, b.published_year, b.page_count, b.created_at,
       b.author_id, a.id, a.first_name, a.last_name, a.bio, a.born_year
FROM books b
INNER JOIN authors a ON b.author_id = a.id
WHERE b.published_year > 2000;
```

The LINQ expression (`.Include()`, `.Where()`, `.OrderBy()`) is an **expression tree** — a description of a query, not the execution of one. EF Core reads this tree and generates SQL at runtime.

---

## Chapter 2 — What Is a DbContext?

`AppDbContext` is the central object in our application. Think of it as having three roles:

### Role 1: The Schema Definer

When you add `DbSet<Author>` and `DbSet<Book>` properties, you are telling EF Core: *"These two C# classes represent database tables."* EF Core reads your model classes, sees the attributes like `[Table("authors")]` and `[MaxLength(100)]`, and knows how to create the schema.

### Role 2: The Change Tracker

When you load a `Book` from the database, EF Core secretly takes a snapshot of its values. When you modify `book.Title = "New Title"` and call `SaveChangesAsync()`, EF Core compares the current values to the snapshot and generates an `UPDATE` statement for only the columns that changed.

```
                    EF Core
      Load Book ──► Snapshots original values
      book.Title = "New Title" ◄── You modify
      SaveChanges() ──► EF Core compares, generates:
                        UPDATE books SET title = 'New Title' WHERE id = 1
```

This is called **Change Tracking**. It means you never write `UPDATE` SQL manually.

### Role 3: The Unit of Work

`SaveChangesAsync()` is a single atomic operation. If you add 10 books in one request and then call `SaveChanges()`, all 10 inserts happen in one database transaction. Either all succeed or all fail together. No partial saves.

### Scoped Lifetime

Notice in `Program.cs`:

```csharp
builder.Services.AddDbContext<AppDbContext>(options => ...);
```

`AddDbContext` registers it as **Scoped** — one instance per HTTP request. This is intentional:

- **Not Singleton**: A single shared `DbContext` across all requests would have race conditions. Change tracking would bleed between requests.
- **Not Transient**: Creating a new connection for every database call would be extremely slow.
- **Scoped** is the sweet spot: one `DbContext` lives for the duration of one HTTP request, then is disposed.

---

## Chapter 3 — What Is a Migration?

When you write `db.Database.Migrate()` on startup, EF Core runs any pending **migrations**. But what is a migration?

A migration is a **snapshot of how the schema should change**. When you add a new property to `Book`, you run:

```bash
dotnet ef migrations add AddPageCount
```

EF Core compares your current model against the last known schema and generates a C# file:

```csharp
public partial class AddPageCount : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "page_count",
            table: "books",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "page_count", table: "books");
    }
}
```

`Up()` applies the change. `Down()` reverses it (for rollbacks). EF Core tracks which migrations have been applied in a special table called `__EFMigrationsHistory`.

### Running Migrations in This Project

For Project 1, run these commands from the `proj1_monolith/` folder:

```bash
# Create the first migration
dotnet ef migrations add InitialCreate

# Apply migrations to the database
dotnet ef database update

# Or just run the app — it calls db.Database.Migrate() on startup
dotnet run
```

> ⚠️ **GOTCHA**: `db.Database.Migrate()` on startup is convenient for learning but dangerous in production. If 10 server instances start at the same time, they all try to migrate simultaneously, causing race conditions. In production, run migrations as a separate step in the deployment pipeline.

---

## Chapter 4 — How the Code Works

### Data Annotations: Two Jobs, One Class

Look at `Models.cs`:

```csharp
[Table("authors")]
public class Author
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Required]
    [MaxLength(100)]
    [Column("first_name")]
    public string FirstName { get; set; } = string.Empty;
    ...
}
```

These attributes serve two purposes:

1. **Database schema** (`[Table]`, `[Column]`, `[MaxLength]`): Tell EF Core how to create the table.
2. **Conceptual documentation** (`[Required]`): Signal that this field must have a value.

This is called the **Active Record pattern** — the data class knows how to persist itself. It's quick and readable for small projects.

### The Endpoint Lambda Pattern

Our endpoints look like:

```csharp
app.MapGet("/books", async (AppDbContext db) =>
{
    var books = await db.Books.Include(b => b.Author)...
    return Results.Ok(books);
});
```

The `(AppDbContext db)` parameter is **automatically injected** by ASP.NET Core's dependency injection. You don't create the `AppDbContext` yourself — ASP.NET Core creates it and hands it to your function. This is a key feature of modern .NET: the framework manages object lifetimes so you don't have to.

### Eager Loading with Include()

When we call:

```csharp
var books = await db.Books.Include(b => b.Author).ToListAsync();
```

EF Core performs a **SQL JOIN** to fetch books and their authors in one round trip. Without `.Include()`, accessing `book.Author` after loading would trigger a separate SQL query for every single book — the notorious **N+1 query problem**. Always think about what data you need upfront and load it eagerly.

---

## Chapter 5 — Honest Pros and Cons

### ✅ What This Approach Gets Right

| Strength | Why it matters |
|---|---|
| **Zero ceremony** | You can understand the entire application in 5 minutes |
| **Easy to prototype** | Write an endpoint in 10 lines of code |
| **No hidden layers** | What you see is what runs — no indirection |
| **Great for scripts and tools** | Small CLI tools, admin scripts, one-off importers |

### ❌ Where This Approach Breaks Down

#### Problem 1: You Cannot Unit Test the Business Logic

The business logic (checking if an author exists before creating a book) is tangled inside the HTTP endpoint. To test it, you must either:
- Start a real database (slow, fragile)
- Start a real HTTP server (even slower)

There is no way to call just the "create book" logic in isolation. In Project 2, we move business logic into a `BookService` class that can be tested with a fake repository and no HTTP.

#### Problem 2: DbContext Leaks Everywhere

Every endpoint receives `AppDbContext` directly. If you have 20 endpoints, all 20 touch the database layer directly. If you want to add logging to every database operation, or wrap every write in a transaction, you have to edit every endpoint. In Project 3, pipeline behaviours solve this in one place.

#### Problem 3: Domain Logic is Scattered and Duplicated

What defines a valid book? A title, an ISBN, an existing author. Right now that knowledge lives inside the `POST /books` endpoint. If you later build a bulk import endpoint, you duplicate that validation. In Project 2, the `BookService` centralises it.

#### Problem 4: Data Annotations Contaminate Your Domain

Your `Author` class has `[Table("authors")]` and `[MaxLength(100)]`. These are database concerns bleeding into what should be a pure business concept. If you ever switch from PostgreSQL to MongoDB, you have to change your domain model. In Project 2, we use the **Fluent API** in a separate configuration class to keep these concerns separate.

---

## Summary: What Level 1 Teaches You

```
Request  →  Program.cs endpoint  →  AppDbContext  →  PostgreSQL
             (validation here)      (query here)
             (business logic here)
```

Everything is in one place. This is the starting point — not the destination. The next project will pull the database access and business logic apart into separate, testable layers.

---

*Next → [Project 2: The Layered Architecture](../proj2_layered/GUIDE.md)*
