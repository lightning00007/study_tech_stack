using BookLibrary.Cqrs.Common;
using BookLibrary.Cqrs.Infrastructure;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BookLibrary.Cqrs.Features.Books.GetBook;

// =============================================================================
// FEATURES/BOOKS/GETBOOK/ — A Query (read-only operation)
// =============================================================================
// In CQRS:
//   - COMMANDS change state (CreateBook, DeleteBook, UpdateBook)
//   - QUERIES read state (GetBook, GetAllBooks, SearchBooks)
//
// WHY SEPARATE THEM?
// Commands and queries have very different characteristics:
//
//   Commands:
//     - Must be transactional (changes must be atomic)
//     - Typically slow (write locks, constraint checks, event publishing)
//     - Happen infrequently
//
//   Queries:
//     - Can be cached
//     - Typically fast (no write locks needed)
//     - Happen very frequently (pages load with multiple queries)
//
// By separating them, you can optimise each independently:
//   - Queries can read from a read replica (faster, no write load)
//   - Queries can be cached in Redis
//   - Commands can have transaction wrapping; queries don't need it
//
// NOTICE: Queries return DTOs, not domain entities.
// Domain entities (Book with private setters, domain methods, etc.) carry
// behaviour. A query just needs data. DTOs are plain data containers,
// optimised for what the client needs — no more, no less.
// =============================================================================

/// <summary>Response DTO for a single book. Contains only what the client needs.</summary>
public sealed record BookDto(
    int Id,
    string Title,
    string Isbn,
    string AuthorName,
    int? PublishedYear,
    int? PageCount,
    DateTime CreatedAt
);

/// <summary>Query to retrieve a single book by its ID.</summary>
public sealed record GetBookQuery(int BookId) : IRequest<Result<BookDto>>;

/// <summary>Handles the GetBookQuery. Reads from the database and returns a DTO.</summary>
public sealed class GetBookQueryHandler : IRequestHandler<GetBookQuery, Result<BookDto>>
{
    private readonly AppDbContext _db;

    public GetBookQueryHandler(AppDbContext db)
    {
        _db = db;
    }

    public async Task<Result<BookDto>> Handle(GetBookQuery query, CancellationToken cancellationToken)
    {
        // 💡 WHY Select() instead of Include() + manual mapping?
        // Using Select() projects the query directly to a DTO IN THE DATABASE.
        // EF Core generates a SELECT with exactly the columns we need —
        // not SELECT * followed by C# mapping. This is more efficient.
        //
        // The SQL generated is roughly:
        // SELECT b.id, b.title, b.isbn, a.first_name || ' ' || a.last_name, ...
        // FROM books b JOIN authors a ON b.author_id = a.id
        // WHERE b.id = @id
        var bookDto = await _db.Books
            .Where(b => b.Id == query.BookId)
            .Select(b => new BookDto(
                b.Id,
                b.Title,
                b.Isbn,
                b.Author.FirstName + " " + b.Author.LastName,
                b.PublishedYear,
                b.PageCount,
                b.CreatedAt
            ))
            .FirstOrDefaultAsync(cancellationToken);

        if (bookDto is null)
            return Result<BookDto>.Failure($"Book with ID {query.BookId} was not found.");

        return Result<BookDto>.Success(bookDto);
    }
}
