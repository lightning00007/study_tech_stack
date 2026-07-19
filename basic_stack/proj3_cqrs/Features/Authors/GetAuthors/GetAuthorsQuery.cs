using BookLibrary.Cqrs.Common;
using BookLibrary.Cqrs.Infrastructure;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BookLibrary.Cqrs.Features.Authors.GetAuthors;

public sealed record AuthorDto(int Id, string FullName, string? Bio, int? BornYear, int BookCount);

public sealed record GetAuthorsQuery : IRequest<Result<IReadOnlyList<AuthorDto>>>;

public sealed class GetAuthorsQueryHandler : IRequestHandler<GetAuthorsQuery, Result<IReadOnlyList<AuthorDto>>>
{
    private readonly AppDbContext _db;

    public GetAuthorsQueryHandler(AppDbContext db) => _db = db;

    public async Task<Result<IReadOnlyList<AuthorDto>>> Handle(GetAuthorsQuery query, CancellationToken cancellationToken)
    {
        var authors = await _db.Authors
            .OrderBy(a => a.LastName)
            .Select(a => new AuthorDto(
                a.Id,
                a.FirstName + " " + a.LastName,
                a.Bio,
                a.BornYear,
                a.Books.Count
            ))
            .ToListAsync(cancellationToken);

        return Result<IReadOnlyList<AuthorDto>>.Success(authors);
    }
}
