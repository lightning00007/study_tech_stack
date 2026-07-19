using BookLibrary.CloudNative.Common;
using BookLibrary.CloudNative.Common.Behaviors;
using BookLibrary.CloudNative.Domain;
using BookLibrary.CloudNative.Infrastructure.Persistence;
using FluentValidation;
using MediatR;

namespace BookLibrary.CloudNative.Features.Authors.CreateAuthor;

public sealed record CreateAuthorCommand(string FirstName, string LastName, string? Bio, int? BornYear)
    : IRequest<Result<int>>, ITransactionalCommand;

public sealed class CreateAuthorCommandValidator : AbstractValidator<CreateAuthorCommand>
{
    public CreateAuthorCommandValidator()
    {
        RuleFor(x => x.FirstName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.LastName).NotEmpty().MaximumLength(100);
    }
}

public sealed class CreateAuthorCommandHandler : IRequestHandler<CreateAuthorCommand, Result<int>>
{
    private readonly AppDbContext _db;
    public CreateAuthorCommandHandler(AppDbContext db) => _db = db;

    public async Task<Result<int>> Handle(CreateAuthorCommand command, CancellationToken cancellationToken)
    {
        var author = Author.Create(command.FirstName, command.LastName, command.Bio, command.BornYear);
        _db.Authors.Add(author);
        return Result<int>.Success(author.Id);
    }
}
