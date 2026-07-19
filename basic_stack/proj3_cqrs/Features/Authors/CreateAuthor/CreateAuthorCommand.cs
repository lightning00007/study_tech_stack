using BookLibrary.Cqrs.Common;
using BookLibrary.Cqrs.Domain;
using BookLibrary.Cqrs.Infrastructure;
using FluentValidation;
using MediatR;

namespace BookLibrary.Cqrs.Features.Authors.CreateAuthor;

// ── COMMAND ───────────────────────────────────────────────────────────────────

/// <summary>Command to create a new author in the library.</summary>
public sealed record CreateAuthorCommand(
    string FirstName,
    string LastName,
    string? Bio,
    int? BornYear
) : IRequest<Result<int>>;

// ── VALIDATOR ─────────────────────────────────────────────────────────────────

public sealed class CreateAuthorCommandValidator : AbstractValidator<CreateAuthorCommand>
{
    public CreateAuthorCommandValidator()
    {
        RuleFor(x => x.FirstName)
            .NotEmpty().WithMessage("First name is required.")
            .MaximumLength(100).WithMessage("First name cannot exceed 100 characters.");

        RuleFor(x => x.LastName)
            .NotEmpty().WithMessage("Last name is required.")
            .MaximumLength(100).WithMessage("Last name cannot exceed 100 characters.");

        RuleFor(x => x.BornYear)
            .InclusiveBetween(1, DateTime.UtcNow.Year)
            .When(x => x.BornYear.HasValue)
            .WithMessage($"Born year must be between 1 and {DateTime.UtcNow.Year}.");
    }
}

// ── HANDLER ───────────────────────────────────────────────────────────────────

public sealed class CreateAuthorCommandHandler : IRequestHandler<CreateAuthorCommand, Result<int>>
{
    private readonly AppDbContext _db;

    public CreateAuthorCommandHandler(AppDbContext db)
    {
        _db = db;
    }

    public async Task<Result<int>> Handle(CreateAuthorCommand command, CancellationToken cancellationToken)
    {
        var author = Author.Create(command.FirstName, command.LastName, command.Bio, command.BornYear);

        _db.Authors.Add(author);
        await _db.SaveChangesAsync(cancellationToken);

        return Result<int>.Success(author.Id);
    }
}
