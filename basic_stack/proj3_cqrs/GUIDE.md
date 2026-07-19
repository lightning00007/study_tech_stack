# 📖 Project 3 — CQRS with MediatR
### *"Commands change state. Queries read state. Keep them separate."*

> *"The mediator pattern defines an object that encapsulates how a set of objects interact. This object promotes loose coupling by keeping objects from referring to each other explicitly."*  
> — Gang of Four, Design Patterns

---

## What Changed from Project 2?

Project 2 introduced layers (Api → Application → Infrastructure). Project 3 keeps those layers but makes a radical change to how the Application layer is organised.

In Project 2, `BookService.cs` handled every book operation: GetAll, GetById, Create, Update, Delete. As the application grows, that one class will handle 20 or 30 operations. It becomes a "God Object" — a class that knows everything and does everything.

Project 3 breaks each operation into its own isolated **vertical slice** using CQRS.

```
Project 2 Application layer:         Project 3 Features (vertical slices):
──────────────────────────           ──────────────────────────────────────────
BookService.cs                        Features/
  - GetAllBooks()                       Books/
  - GetBookById()                         CreateBook/
  - CreateBook()                            CreateBookCommand.cs   ← ALL of create
  - UpdateBook()                          GetBook/
  - DeleteBook()                            GetBookQuery.cs        ← ALL of get one
  - PublishBook()                         GetBooks/
  - ArchiveBook()                           GetBooksQuery.cs       ← ALL of get all
  - SearchBooks()
  - ...
```

---

## Chapter 1 — The Mediator Pattern

The Mediator pattern is one of the 23 classic Gang of Four design patterns. The idea: instead of components communicating **directly** with each other, they communicate through a **central mediator**.

### Without MediatR (Project 2)

```
BooksController  ──► IBookService  ──► IBookRepository
                 direct dependency   direct dependency
```

The controller must know about the service. The service must know about the repository. Changing the service's interface means changing the controller.

### With MediatR (Project 3)

```
BooksController ──► IMediator.Send(CreateBookCommand)
                                    │
                         MediatR resolves handler
                                    │
                         CreateBookCommandHandler ──► DbContext
```

The controller only knows about MediatR. The handler only knows about the database. Neither knows about each other. The controller doesn't import the handler's namespace. The handler doesn't import the controller's namespace.

MediatR acts as the **message bus**:
- Controllers **send** messages (Commands and Queries)
- Handlers **receive** messages and act on them
- MediatR **routes** messages to the right handler

---

## Chapter 2 — Commands vs Queries (CQRS)

CQRS stands for **Command Query Responsibility Segregation**, coined by Greg Young building on Bertrand Meyer's Command Query Separation principle.

The rule is simple: **a method either changes state OR returns data. Never both.**

### Commands — Intent to Change State

```csharp
// A Command is a request to DO something
public sealed record CreateBookCommand(
    string Title,
    string Isbn,
    int AuthorId,
    int? PublishedYear,
    int? PageCount
) : IRequest<Result<int>>;
```

Commands are **verbs** in the past or present tense: `CreateBook`, `DeleteAuthor`, `PublishChapter`. They return a `Result` (success or failure), but not the changed data itself — the ID of the new record at most.

Commands express **intent**: "I want you to create a book." They do NOT say "please return the book you created" — that's a query's job.

### Queries — Request for Data

```csharp
// A Query asks for information without changing anything
public sealed record GetBookQuery(int BookId) : IRequest<Result<BookDto>>;
```

Queries are **nouns** or questions: `GetBook`, `GetAllBooks`, `SearchBooksByTitle`. They return data (DTOs) and must never modify state.

### Why Does the Separation Matter?

1. **Optimisation**: Commands need write locks and transactions. Queries don't. With CQRS, you can route queries to a read replica database without changing any application code.

2. **Caching**: Queries return data. You can cache the data with a `CachingBehavior`. Commands change data — you invalidate relevant caches after a command succeeds.

3. **Event Sourcing** (Project 4): Commands can be logged as events and replayed. Queries just read the current projected state.

4. **Clarity**: When you see `GetBookQuery`, you immediately know it reads data and never writes. When you see `CreateBookCommand`, you know it changes something.

---

## Chapter 3 — MediatR Pipeline Behaviours

This is where MediatR shines. Behaviours are middleware for your business operations.

### The Pipeline

Every command and query passes through this chain:

```
Incoming Command/Query
        │
        ▼
┌───────────────────────────────────────┐
│           LoggingBehavior             │  ← [START] CreateBookCommand
│  ┌────────────────────────────────┐   │
│  │       ValidationBehavior       │   │  ← Run validators; fail fast
│  │  ┌─────────────────────────┐   │   │
│  │  │  CreateBookCommandHandler │   │   │  ← Your business logic
│  │  └─────────────────────────┘   │   │
│  └────────────────────────────────┘   │
└───────────────────────────────────────┘
        │
        ▼
Response (Result<int>)
        │
        ▼
LoggingBehavior ← [END] CreateBookCommand completed in 42ms
```

### The Open/Closed Principle in Action

**Open for extension, closed for modification.** Every handler is closed — you don't touch it to add logging or validation. But the pipeline is open — you add behaviours and they automatically wrap every handler.

Want to add caching to all queries? Add a `CachingBehavior`. Want to add error handling? Add an `ExceptionBehavior`. Your handlers never change.

### Writing a Behaviour

```csharp
public class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next, // ← the next step in the pipeline
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("[START] {RequestName}", typeof(TRequest).Name);

        var response = await next(); // ← calls the next behaviour or the handler

        _logger.LogInformation("[END] {RequestName}", typeof(TRequest).Name);
        return response;
    }
}
```

The `next` delegate is the crucial piece — calling it passes control down the chain. If you don't call `next()`, the handler never runs (useful for validation failures).

---

## Chapter 4 — FluentValidation

FluentValidation moves validation rules into dedicated, testable validator classes.

### The Problem FluentValidation Solves

In Project 1, validation was this:

```csharp
// Project 1 — Scattered manual validation
if (string.IsNullOrWhiteSpace(request.Title))
    return Results.BadRequest("Title is required.");
if (string.IsNullOrWhiteSpace(request.Isbn))
    return Results.BadRequest("ISBN is required.");
if (request.Isbn.Length > 20)
    return Results.BadRequest("ISBN cannot exceed 20 characters.");
// ...more if/else...
```

Problems: it's in the endpoint, it's duplicated if you have multiple entry points, and you have to manually test each rule.

### FluentValidation Approach

```csharp
public class CreateBookCommandValidator : AbstractValidator<CreateBookCommand>
{
    public CreateBookCommandValidator()
    {
        RuleFor(x => x.Title)
            .NotEmpty().WithMessage("Book title is required.")
            .MaximumLength(200).WithMessage("Title cannot exceed 200 characters.");

        RuleFor(x => x.Isbn)
            .NotEmpty()
            .MaximumLength(20)
            .Matches(@"^[0-9\-]+$").WithMessage("ISBN must contain only digits and hyphens.");
    }
}
```

Benefits:
- **Centralised**: All rules for `CreateBookCommand` in one class
- **Testable**: `new CreateBookCommandValidator().Validate(command)` — no HTTP, no database
- **Composable**: Rules can share common rulesets with `.Include()`
- **Automatic**: `ValidationBehavior` discovers and runs it automatically

---

## Chapter 5 — The Result\<T\> Type

See `Common/Result.cs`. This is the evolved, monadic version of Project 2's `ServiceResult<T>`.

### The Map and Bind Operations

The `Result<T>` type supports functional-style chaining:

```csharp
// Without Map/Bind — imperative style
var bookResult = await GetBook(id);
if (bookResult.IsFailure) return Result<string>.Failure(bookResult.Error!);
var titleResult = await GetTitle(bookResult.Value!);
if (titleResult.IsFailure) return Result<string>.Failure(titleResult.Error!);
return Result<string>.Success(titleResult.Value!.ToUpper());

// With Bind — declarative style (same logic, no null/failure checks)
var result = await GetBook(id)
    .Bind(async book => await GetTitle(book))
    .Map(title => title.ToUpper());
```

The `Bind` and `Map` operations short-circuit on failure: if `GetBook` fails, `GetTitle` is never called and the error propagates automatically.

---

## Chapter 6 — Vertical Slice Architecture

Project 3 uses the folder structure pioneered by Jimmy Bogard (the creator of MediatR):

```
Features/
├── Books/
│   ├── CreateBook/
│   │   └── CreateBookCommand.cs   ← Command + Validator + Handler all here
│   ├── GetBook/
│   │   └── GetBookQuery.cs        ← Query + Handler all here
│   └── GetBooks/
│       └── GetBooksQuery.cs
└── Authors/
    ├── CreateAuthor/
    │   └── CreateAuthorCommand.cs
    └── GetAuthors/
        └── GetAuthorsQuery.cs
```

**The rule**: everything needed to implement one use case lives in one folder.

### Contrast with Horizontal Slices (Project 2)

```
Application/
├── IBookRepository.cs  ← interface for books
├── IAuthorRepository.cs ← interface for authors
├── BookService.cs       ← all book logic
└── AuthorService.cs     ← all author logic
```

With horizontal slices, adding "Publish a Book" touches:
- `BookService.cs` (add `PublishBook()`)
- `IBookService.cs` (add method to interface)
- `BooksController.cs` (add endpoint)

With vertical slices, adding "Publish a Book" means:
- Create `Features/Books/PublishBook/PublishBookCommand.cs`
- Done. No existing files touched.

---

## Chapter 7 — Honest Pros and Cons

### ✅ What Project 3 Gets Right

| Strength | Why it matters |
|---|---|
| **Handlers are tiny** | `CreateBookCommandHandler` is ~30 lines. Easy to understand. |
| **Zero coupling** | Controller doesn't know the handler exists. Handler doesn't know the controller exists. |
| **Cross-cutting via behaviours** | Add logging/caching/auth once. Applies everywhere. |
| **Validation is co-located** | `CreateBookValidator.cs` is right next to `CreateBookCommand.cs` |
| **Open/Closed Principle** | Add features without modifying existing ones |

### ❌ What Project 3 Still Doesn't Have

#### Missing: Transaction Safety

What if `CreateBookCommandHandler` saves to the database successfully, and then something downstream (a notification, a secondary save) fails? The book is in the database but the system is in an inconsistent state.

Project 4 adds a `TransactionBehavior` that wraps every command in a database transaction. Success = commit. Exception = rollback. Automatic and invisible to the handler.

#### Missing: Guaranteed Event Delivery

What if "Book Published" event needs to notify external systems (email service, search indexer)? If we publish the event to AWS SNS and then the application crashes, the event is lost. Project 4 introduces the **Outbox Pattern** — events are saved to the database in the same transaction as the business data, guaranteeing they will eventually be delivered.

#### Missing: Aggregate Roots and Domain Events

Books and Authors are plain classes. They have no way to say "something significant happened to me." Project 4 introduces `AggregateRoot<TId>` with domain events — when a book is published, it raises a `BookPublishedEvent` internally, which is then delivered via SNS.

---

## Summary: What Project 3 Teaches You

```
HTTP Request
    │
    ▼
Controller ──► IMediator.Send(CreateBookCommand)
                    │
                    │  LoggingBehavior (cross-cutting)
                    │    ValidationBehavior (cross-cutting)
                    │      CreateBookCommandHandler (business logic)
                    │      ↕ DbContext ↕ PostgreSQL
                    │
                    ▼
           Result<int> (Success(42) or Failure("error"))
```

Each handler is a small, focused class. Adding a feature is creating a new class — not modifying an existing one. Cross-cutting concerns wrap everything without touching individual handlers.

---

*Next → [Project 4: Cloud-Native with AWS](../proj4_cloud/GUIDE.md)*
