using BookLibrary.CloudNative.Common;
using BookLibrary.CloudNative.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BookLibrary.CloudNative.Features.Books.GetBooks;

public sealed record BookDto(int Id, string Title, string Isbn, bool IsPublished, string AuthorName, int? PublishedYear, int? PageCount, DateTime CreatedAt);
public sealed record GetBooksQuery : IRequest<Result<IReadOnlyList<BookDto>>>;

public sealed class GetBooksQueryHandler : IRequestHandler<GetBooksQuery, Result<IReadOnlyList<BookDto>>>
{
    private readonly AppDbContext _db;
    public GetBooksQueryHandler(AppDbContext db) => _db = db;

    public async Task<Result<IReadOnlyList<BookDto>>> Handle(GetBooksQuery _, CancellationToken ct)
    {
        var books = await _db.Books
            .OrderBy(b => b.Title)
            .Select(b => new BookDto(b.Id, b.Title, b.Isbn, b.IsPublished,
                b.Author.FirstName + " " + b.Author.LastName,
                b.PublishedYear, b.PageCount, b.CreatedAt))
            .ToListAsync(ct);
        return Result<IReadOnlyList<BookDto>>.Success(books);
    }
}
