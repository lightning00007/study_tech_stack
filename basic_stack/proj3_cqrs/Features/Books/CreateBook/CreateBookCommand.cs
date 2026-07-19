using BookLibrary.Cqrs.Common;
using BookLibrary.Cqrs.Domain;
using BookLibrary.Cqrs.Infrastructure;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BookLibrary.Cqrs.Features.Books.CreateBook;

// =============================================================================
// FEATURES/BOOKS/CREATEBOOK/ — A vertical slice
// =============================================================================
// Everything related to "Create a Book" lives in this one folder:
//   - The Command (what to do)
//   - The Validator (is the input valid?)
//   - The Handler (how to do it)
//
// This is the "Vertical Slice Architecture" — organise by FEATURE, not by TYPE.
//
// Compare with Project 2's horizontal slice:
//
//   Project 2 (horizontal — by type):
//     Application/BookService.cs        ← ALL book operations here
//     Infrastructure/BookRepository.cs  ← ALL book queries here
//     Api/BooksController.cs            ← ALL book endpoints here
//
//   Project 3 (vertical — by feature):
//     Features/Books/CreateBook/CreateBookCommand.cs  ← ONE operation, isolated
//     Features/Books/GetBook/GetBookQuery.cs
//     Features/Books/GetBooks/GetBooksQuery.cs
//
// With vertical slices:
//   - Finding code is trivial: "I'm working on CreateBook? It's all in one folder."
//   - Adding a feature doesn't touch existing features (no risk of regression).
//   - Deleting a feature is one folder delete.
// =============================================================================

// ── COMMAND (the "what") ──────────────────────────────────────────────────────

/// <summary>
/// Command to create a new book. Implements IRequest to signal that MediatR
/// should route it to a handler and return a Result&lt;int&gt;.
/// </summary>
/// <remarks>
/// Using 'record' gives us:
///   - Immutability (all properties are init-only by default)
///   - Value equality (two commands with same values are equal — useful in tests)
///   - Concise syntax (primary constructor generates properties automatically)
/// </remarks>
public sealed record CreateBookCommand(
    string Title,
    string Isbn,
    int AuthorId,
    int? PublishedYear,
    int? PageCount
) : IRequest<Result<int>>;

// ── VALIDATOR (the "is it valid?") ───────────────────────────────────────────

/// <summary>
/// Validates the CreateBookCommand before the handler runs.
/// Discovered automatically by FluentValidation's assembly scanning.
/// </summary>
public sealed class CreateBookCommandValidator : AbstractValidator<CreateBookCommand>
{
    public CreateBookCommandValidator()
    {
        RuleFor(x => x.Title)
            .NotEmpty().WithMessage("Book title is required.")
            .MaximumLength(200).WithMessage("Title cannot exceed 200 characters.");

        RuleFor(x => x.Isbn)
            .NotEmpty().WithMessage("ISBN is required.")
            .MaximumLength(20).WithMessage("ISBN cannot exceed 20 characters.")
            .Matches(@"^[0-9\-]+$").WithMessage("ISBN must contain only digits and hyphens.");

        RuleFor(x => x.AuthorId)
            .GreaterThan(0).WithMessage("A valid Author ID is required.");

        RuleFor(x => x.PublishedYear)
            .InclusiveBetween(1000, DateTime.UtcNow.Year + 1)
            .When(x => x.PublishedYear.HasValue)
            .WithMessage($"Published year must be between 1000 and {DateTime.UtcNow.Year + 1}.");

        RuleFor(x => x.PageCount)
            .GreaterThan(0)
            .When(x => x.PageCount.HasValue)
            .WithMessage("Page count must be a positive number.");
    }
}

// ── HANDLER (the "how") ──────────────────────────────────────────────────────

/// <summary>
/// Handles the CreateBookCommand. Contains the business logic for book creation.
/// By the time Handle() runs, the input has already been validated by
/// CreateBookCommandValidator via the ValidationBehavior pipeline.
/// </summary>
public sealed class CreateBookCommandHandler : IRequestHandler<CreateBookCommand, Result<int>>
{
    private readonly AppDbContext _db;

    // 💡 WHY inject DbContext directly instead of a repository?
    // In a CQRS system with vertical slices, each handler only does one specific thing.
    // Injecting DbContext directly is simpler and more efficient than adding
    // a full repository abstraction for every operation.
    // This is a deliberate trade-off: we lose the in-memory testability of
    // repositories, but gain simplicity and direct query control.
    //
    // For integration tests, we use a real test database (or SQLite in-memory mode).
    public CreateBookCommandHandler(AppDbContext db)
    {
        _db = db;
    }

    public async Task<Result<int>> Handle(CreateBookCommand command, CancellationToken cancellationToken)
    {
        // Business rule 1: Author must exist
        var author = await _db.Authors.FindAsync([command.AuthorId], cancellationToken);
        if (author is null)
            return Result<int>.Failure($"Author with ID {command.AuthorId} does not exist.");

        // Business rule 2: ISBN must be unique
        var isbnTaken = await _db.Books.AnyAsync(b => b.Isbn == command.Isbn, cancellationToken);
        if (isbnTaken)
            return Result<int>.Failure($"A book with ISBN '{command.Isbn}' already exists.");

        // Create the domain object via the factory method
        var book = Book.Create(command.Title, command.Isbn, author, command.PublishedYear, command.PageCount);

        _db.Books.Add(book);
        await _db.SaveChangesAsync(cancellationToken);

        return Result<int>.Success(book.Id);
    }
}
