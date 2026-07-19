using BookLibrary.CloudNative.Common;
using BookLibrary.CloudNative.Common.Behaviors;
using BookLibrary.CloudNative.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BookLibrary.CloudNative.Features.Books.PublishBook;

// =============================================================================
// FEATURES/BOOKS/PUBLISHBOOK/ — Demonstrates domain event lifecycle
// =============================================================================
// This feature didn't exist in Projects 1-3. It showcases the power of
// domain events: when Book.Publish() is called, it raises BookPublishedEvent
// internally. The outbox captures it. SNS delivers it to subscribers.
//
// The handler is blissfully unaware of SNS, queues, or event publishing.
// It just calls a domain method. The architecture handles the rest.
// =============================================================================

public sealed record PublishBookCommand(int BookId) : IRequest<Result>, ITransactionalCommand;

public sealed class PublishBookCommandHandler : IRequestHandler<PublishBookCommand, Result>
{
    private readonly AppDbContext _db;

    public PublishBookCommandHandler(AppDbContext db) => _db = db;

    public async Task<Result> Handle(PublishBookCommand command, CancellationToken cancellationToken)
    {
        var book = await _db.Books
            .Include(b => b.Author) // Needed because BookPublishedEvent includes AuthorName
            .FirstOrDefaultAsync(b => b.Id == command.BookId, cancellationToken);

        if (book is null)
            return Result.Failure($"Book with ID {command.BookId} was not found.");

        try
        {
            // This call:
            //   1. Sets IsPublished = true on the book
            //   2. Raises BookPublishedEvent (stored in book.DomainEvents)
            // TransactionBehavior will then call SaveChangesAsync(), which:
            //   3. Writes the updated book to the 'books' table
            //   4. Converts BookPublishedEvent to an OutboxMessage row
            //   5. Commits both in one atomic transaction
            // Later, OutboxPublisherJob:
            //   6. Reads the OutboxMessage and publishes it to SNS
            book.Publish();
            return Result.Success();
        }
        catch (InvalidOperationException ex)
        {
            // Guard clause violation (e.g., "book is already published")
            return Result.Failure(ex.Message);
        }
    }
}
