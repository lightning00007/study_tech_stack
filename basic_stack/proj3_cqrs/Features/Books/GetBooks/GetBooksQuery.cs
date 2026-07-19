using BookLibrary.Cqrs.Common;
using BookLibrary.Cqrs.Features.Books.GetBook;
using BookLibrary.Cqrs.Infrastructure;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BookLibrary.Cqrs.Features.Books.GetBooks;

/// <summary>Query to retrieve all books in the library.</summary>
public sealed record GetBooksQuery : IRequest<Result<IReadOnlyList<BookDto>>>;

/// <summary>Handles GetBooksQuery.</summary>
public sealed class GetBooksQueryHandler : IRequestHandler<GetBooksQuery, Result<IReadOnlyList<BookDto>>>
{
    private readonly AppDbContext _db;

    public GetBooksQueryHandler(AppDbContext db)
    {
        _db = db;
    }

    public async Task<Result<IReadOnlyList<BookDto>>> Handle(GetBooksQuery query, CancellationToken cancellationToken)
    {
        var books = await _db.Books
            .OrderBy(b => b.Title)
            .Select(b => new BookDto(
                b.Id,
                b.Title,
                b.Isbn,
                b.Author.FirstName + " " + b.Author.LastName,
                b.PublishedYear,
                b.PageCount,
                b.CreatedAt
            ))
            .ToListAsync(cancellationToken);

        return Result<IReadOnlyList<BookDto>>.Success(books);
    }
}
