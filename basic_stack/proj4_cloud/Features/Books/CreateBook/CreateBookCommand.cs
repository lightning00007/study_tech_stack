using BookLibrary.CloudNative.Common;
using BookLibrary.CloudNative.Common.Behaviors;
using BookLibrary.CloudNative.Domain;
using BookLibrary.CloudNative.Infrastructure.Persistence;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BookLibrary.CloudNative.Features.Books.CreateBook;

// =============================================================================
// FEATURES/BOOKS/CREATEBOOK/ — Full clean architecture vertical slice
// =============================================================================
// This slice is nearly identical to Project 3's version, with two key additions:
//
//   1. ITransactionalCommand — opts this command into TransactionBehavior wrapping
//      The handler no longer calls SaveChangesAsync() — the behaviour handles it.
//
//   2. Domain events — Book.Create() raises BookCreatedEvent internally.
//      DbContext.SaveChangesAsync() intercepts this and writes it to the outbox.
//      OutboxPublisherJob eventually publishes it to SNS.
//
// The handler doesn't know any of this is happening. It calls Book.Create(),
// adds to the DbSet, and returns. The infrastructure does the rest.
// This is the power of well-designed abstractions.
// =============================================================================

public sealed record CreateBookCommand(
    string Title,
    string Isbn,
    int AuthorId,
    int? PublishedYear,
    int? PageCount
) : IRequest<Result<int>>, ITransactionalCommand; // ← Opts into transaction wrapping

public sealed class CreateBookCommandValidator : AbstractValidator<CreateBookCommand>
{
    public CreateBookCommandValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Isbn).NotEmpty().MaximumLength(20).Matches(@"^[0-9\-]+$");
        RuleFor(x => x.AuthorId).GreaterThan(0);
        RuleFor(x => x.PublishedYear).InclusiveBetween(1000, DateTime.UtcNow.Year + 1).When(x => x.PublishedYear.HasValue);
        RuleFor(x => x.PageCount).GreaterThan(0).When(x => x.PageCount.HasValue);
    }
}

public sealed class CreateBookCommandHandler : IRequestHandler<CreateBookCommand, Result<int>>
{
    private readonly AppDbContext _db;

    public CreateBookCommandHandler(AppDbContext db) => _db = db;

    public async Task<Result<int>> Handle(CreateBookCommand command, CancellationToken cancellationToken)
    {
        var author = await _db.Authors.FindAsync([command.AuthorId], cancellationToken);
        if (author is null)
            return Result<int>.Failure($"Author with ID {command.AuthorId} does not exist.");

        var isbnTaken = await _db.Books.AnyAsync(b => b.Isbn == command.Isbn, cancellationToken);
        if (isbnTaken)
            return Result<int>.Failure($"A book with ISBN '{command.Isbn}' already exists.");

        // 📖 CONCEPT: Book.Create() raises BookCreatedEvent internally.
        // The handler doesn't know about events. The domain class does.
        // DbContext.SaveChangesAsync() (called by TransactionBehavior) will
        // intercept the event and write it to outbox_messages.
        var book = Book.Create(command.Title, command.Isbn, author, command.PublishedYear, command.PageCount);
        _db.Books.Add(book);

        // 💡 WHY: We do NOT call SaveChangesAsync() here!
        // TransactionBehavior (registered in Program.cs) calls it AFTER this
        // method returns, wrapping everything in a transaction.
        return Result<int>.Success(book.Id);
    }
}
