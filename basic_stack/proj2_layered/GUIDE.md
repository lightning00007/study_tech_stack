# 📖 Project 2 — The Layered Architecture
### *"Separate concerns. Inject dependencies."*

> *"A class should have only one reason to change."*  
> — Robert C. Martin, The Single Responsibility Principle

---

## What Changed from Project 1?

Open both projects side by side. The domain — books and authors — is identical. The database is the same PostgreSQL structure. But the *organisation* of the code is fundamentally different.

In Project 1, the endpoint lambda was responsible for:
- Parsing the request ✓
- Validating inputs ✓
- Checking business rules ✓
- Executing the database query ✓
- Returning the HTTP response ✓

Five responsibilities in one place. Project 2 separates these into **three distinct layers**, each with a single responsibility.

```
Project 1 (Monolith)        Project 2 (Layered)
──────────────────          ──────────────────────────────
  Program.cs                  Api/
  (does everything)             BooksController.cs  ← HTTP only
                              Application/
                                BookService.cs      ← Business logic only
                              Infrastructure/
                                BookRepository.cs   ← Database only
```

---

## Chapter 1 — The Problem with Data Annotations on Domain Classes

In Project 1, the `Author` class looked like this:

```csharp
// Project 1 — Domain class contaminated by database concerns
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
```

Every attribute (`[Table]`, `[Column]`, `[MaxLength]`) is a database concern. The `Author` class is supposed to model the business concept of an author — a person who writes books. Why does it need to know that the database column is called "first_name"?

This is called **tight coupling** between the domain model and the database schema. The problems are:

1. **You can't use the domain class without EF Core**. If you want to write a unit test for an Author business rule, you need EF Core installed. A pure domain concept has a dependency on a database framework.

2. **Changing the database schema requires changing the domain class**. If PostgreSQL moves the `first_name` column to `given_name`, you change `Author.cs` — your business object — just to rename a column.

3. **The class serves two masters**. It must satisfy both the business rules and the ORM mapping requirements. When those requirements conflict, you compromise.

### The Fix: Fluent API in Separate Configuration Classes

In Project 2, `Author.cs` has zero database attributes:

```csharp
// Project 2 — Pure domain class
public class Author
{
    public int Id { get; private set; }
    public string FirstName { get; private set; }
    // ... no [Table], no [Column], no [Required] attributes
}
```

All the mapping lives in `AuthorConfiguration.cs` in the Infrastructure layer:

```csharp
// Project 2 — Mapping configuration in the right place
public class AuthorConfiguration : IEntityTypeConfiguration<Author>
{
    public void Configure(EntityTypeBuilder<Author> builder)
    {
        builder.ToTable("authors");
        builder.Property(a => a.FirstName).HasColumnName("first_name").HasMaxLength(100);
        // ...
    }
}
```

Now changing the database column name only changes the configuration file — the `Author` domain class is untouched.

---

## Chapter 2 — What Is Dependency Injection?

Dependency Injection (DI) is one of the most important patterns in modern software. It sounds complex, but the idea is simple: **don't create the things you depend on — receive them from the outside**.

### Without DI (Project 1 style)

```csharp
// 😰 Without DI — BookService creates its own dependencies
public class BookService
{
    private readonly BookRepository _repository;

    public BookService()
    {
        // BookService creates BookRepository itself.
        // This means BookService IS tightly coupled to BookRepository.
        // To test BookService, you MUST use BookRepository, which means
        // you MUST have a database running.
        _repository = new BookRepository(new AppDbContext(...));
    }
}
```

### With DI (Project 2 style)

```csharp
// 😌 With DI — BookService receives its dependencies
public class BookService : IBookService
{
    private readonly IBookRepository _repository;

    public BookService(IBookRepository repository)
    {
        // BookService doesn't know WHERE _repository comes from.
        // In production: the DI container injects a real BookRepository.
        // In tests:      you inject a fake InMemoryBookRepository.
        _repository = repository;
    }
}
```

The DI container (`builder.Services` in Program.cs) is the "factory" that creates everything:

```csharp
// Program.cs — Registering the wiring
builder.Services.AddScoped<IBookRepository, BookRepository>();
builder.Services.AddScoped<IBookService, BookService>();
```

When ASP.NET Core needs to handle a `POST /api/books` request, it:
1. Creates a `BooksController`
2. Sees it needs an `IBookService` → creates a `BookService`
3. Sees `BookService` needs an `IBookRepository` → creates a `BookRepository`
4. Sees `BookRepository` needs an `AppDbContext` → creates one (Scoped, shared for the request)

All of this happens automatically. You never call `new BookService(new BookRepository(...))`.

### Lifetimes: Singleton, Scoped, Transient

| Lifetime | Created | Disposed | Use for |
|---|---|---|---|
| **Singleton** | Once, on first use | App shutdown | Configuration, caches |
| **Scoped** | Once per HTTP request | Request ends | DbContext, repositories, services |
| **Transient** | Every time requested | When caller is disposed | Lightweight, stateless utilities |

`DbContext` must be **Scoped** — one per request — because EF Core's change tracker is not thread-safe. If it were Singleton, changes from request A could bleed into request B.

---

## Chapter 3 — What Is the Repository Pattern?

The Repository pattern creates an **abstraction layer over your data access**. Instead of your business logic directly calling EF Core, it talks to a repository interface.

```
BookService  →  IBookRepository  ←→  BookRepository  →  AppDbContext  →  PostgreSQL
(business)       (contract)            (EF Core impl)      (ORM)           (database)
```

The repository has two jobs:
1. **Translate business intent into database queries** — "give me the book with this ID" becomes `SELECT * FROM books WHERE id = @id`
2. **Hide EF Core from the rest of the application** — `BookService` never imports `Microsoft.EntityFrameworkCore`

### Does EF Core Make Repositories Redundant?

This is a fair debate. EF Core's `DbContext` is *already* a Unit of Work and *already* a generic repository. Many experienced developers skip the extra repository layer and inject `DbContext` directly into services.

The reasons to keep repositories in a learning project:

1. **Testability without a database**: With an `IBookRepository` interface, you can write a `FakeBookRepository : IBookRepository` that uses `List<Book>` in memory. Your unit tests run in milliseconds with no database required.

2. **Encapsulation of complex queries**: A method named `GetBooksWithLowInventoryAsync()` is far more readable than the LINQ expression it hides.

3. **Preparation for dual data access**: If a performance-critical query needs raw SQL (via Dapper), you add it inside the repository without changing any of the service code.

---

## Chapter 4 — The Service Layer: Business Logic Centralised

Look at `BookService.CreateBookAsync()`:

```csharp
public async Task<ServiceResult<int>> CreateBookAsync(CreateBookDto dto, CancellationToken ct)
{
    // Business rule 1: The author must exist
    var author = await _authorRepository.GetByIdAsync(dto.AuthorId, ct);
    if (author is null)
        return ServiceResult<int>.Fail($"Author with ID {dto.AuthorId} does not exist.");

    // Business rule 2: ISBN must be unique
    var isbnTaken = await _bookRepository.IsbnExistsAsync(dto.Isbn, ct);
    if (isbnTaken)
        return ServiceResult<int>.Fail($"A book with ISBN '{dto.Isbn}' already exists.");

    // Delegate creation to the domain factory
    var book = Book.Create(dto.Title, dto.Isbn, author, dto.PublishedYear, dto.PageCount);

    await _bookRepository.AddAsync(book, ct);
    await _bookRepository.SaveChangesAsync(ct);

    return ServiceResult<int>.Success(book.Id);
}
```

This service method can be called from:
- An HTTP controller (as in this project)
- A background job that bulk-imports books from a CSV
- A gRPC endpoint
- A CLI command

The business rules (author must exist, ISBN must be unique) are written once and reused everywhere. This is the point.

### ServiceResult\<T\>: A Glimpse of Error Handling

Notice the `ServiceResult<T>` type. Instead of:
- **Returning null** (requires callers to remember to null-check)
- **Throwing exceptions** (expensive, flow-control-through-exceptions is hard to follow)

We return a wrapper that explicitly signals success or failure:

```csharp
public record ServiceResult<T>(bool IsSuccess, T? Value, string? Error)
{
    public static ServiceResult<T> Success(T value) => new(true, value, null);
    public static ServiceResult<T> Fail(string error) => new(false, default, error);
}
```

The caller is forced to check `result.IsSuccess` before using `result.Value`. This pattern becomes the full `Result<T>` type in Project 3.

---

## Chapter 5 — Honest Pros and Cons

### ✅ What Project 2 Gets Right

| Strength | Why it matters |
|---|---|
| **Testable business logic** | `BookService` can be unit-tested with a fake repository, no DB required |
| **Clean domain classes** | `Author` and `Book` have no framework dependencies |
| **Fluent API mapping** | Rich, expressive schema configuration that Data Annotations can't match |
| **Stable API contract** | Controllers depend on interfaces — swap the implementation without touching controllers |

### ❌ What Project 2 Still Gets Wrong

#### Problem 1: Horizontal Slice vs Vertical Slice

Project 2 organises code **horizontally** by type:
```
Application/    ← all services together
Infrastructure/ ← all repositories together  
Api/            ← all controllers together
```

When you add a "Publish Book" feature, you touch files in ALL three folders. Finding all the code related to "publishing a book" requires jumping between three directories.

Project 3 introduces **vertical slices** — all the code for one feature lives in one folder.

#### Problem 2: No Cross-Cutting Concerns

What if you want to log every service method call? You'd add logging to every method individually. What if you want to wrap every write operation in a database transaction? Same problem.

In Project 3, **MediatR pipeline behaviours** solve this by running middleware around every command and query — write it once, it applies everywhere.

#### Problem 3: Service Layer Can Get Fat

As the application grows, `BookService` accumulates every feature related to books. "Publish a book", "archive a book", "search books", "export books to CSV" — all in one class. In Project 3, each use case gets its own isolated handler class.

---

## Summary: What Project 2 Teaches You

```
HTTP Request
    │
    ▼
Api/BooksController  ←── injects ──► IBookService
    │ delegates to
    ▼
Application/BookService  ←── injects ──► IBookRepository, IAuthorRepository
    │ delegates to
    ▼
Infrastructure/BookRepository  ←── uses ──► AppDbContext  →  PostgreSQL
```

Each layer knows only about the layer directly below it — and only through interfaces. This is the foundation that all subsequent patterns build upon.

---

*Next → [Project 3: CQRS with MediatR](../proj3_cqrs/GUIDE.md)*
