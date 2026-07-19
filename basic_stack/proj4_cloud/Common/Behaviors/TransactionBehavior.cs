using BookLibrary.CloudNative.Infrastructure.Persistence;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BookLibrary.CloudNative.Common.Behaviors;

// =============================================================================
// COMMON/BEHAVIORS/TRANSACTIONBEHAVIOR.CS — Database transaction wrapping
// =============================================================================
// This behaviour is new in Project 4 (not in Project 3).
//
// PROBLEM IT SOLVES:
// In Projects 1-3, handlers call SaveChangesAsync() themselves.
// This is fine for simple cases, but consider a handler that:
//   1. Creates a Book
//   2. Updates an author's book count
//   3. Sends a notification (which also writes to DB)
//
// If step 3 fails, steps 1 and 2 are already committed. The database is
// partially updated. This is called a "partial commit" and causes silent
// data corruption.
//
// SOLUTION: Wrap every command in a database transaction. The handler
// never calls SaveChangesAsync() — the behaviour calls it AFTER the
// handler returns, as one atomic operation.
//
// HOW COMMANDS ARE DISTINGUISHED FROM QUERIES:
// We use a marker interface: ITransactionalCommand.
// Commands implement it. Queries do not.
// If the request doesn't implement ITransactionalCommand, we skip the wrapping.
//
// This keeps queries lightweight — no unnecessary transaction overhead for reads.
// =============================================================================

/// <summary>
/// Marker interface for commands that need database transaction wrapping.
/// Implement this on your command records to opt in to transaction management.
/// </summary>
public interface ITransactionalCommand { }

/// <summary>
/// Wraps ITransactionalCommand requests in a database transaction.
/// Commits on success. On exception, EF Core rolls back automatically
/// (the transaction is disposed without commit).
/// </summary>
public class TransactionBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly AppDbContext _db;
    private readonly ILogger<TransactionBehavior<TRequest, TResponse>> _logger;

    public TransactionBehavior(AppDbContext db, ILogger<TransactionBehavior<TRequest, TResponse>> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        // Non-transactional requests (Queries) pass through unchanged
        if (request is not ITransactionalCommand)
            return await next();

        var requestName = typeof(TRequest).Name;
        _logger.LogDebug("[Transaction] Starting transaction for {RequestName}", requestName);

        // 📖 CONCEPT: BeginTransactionAsync() opens an explicit database transaction.
        // The 'using' statement ensures the transaction is disposed (and rolled back
        // if not committed) even if an exception is thrown.
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            // Run the handler (our business logic)
            // ⚠️ GOTCHA: Handlers must NOT call SaveChangesAsync() themselves.
            // We call it here so that the Outbox domain events are also included
            // in the same transaction.
            var response = await next();

            // SaveChanges writes business entities + outbox messages (see AppDbContext)
            await _db.SaveChangesAsync(cancellationToken);

            // Commit everything atomically
            await transaction.CommitAsync(cancellationToken);

            _logger.LogDebug("[Transaction] Committed transaction for {RequestName}", requestName);
            return response;
        }
        catch (Exception ex)
        {
            // The 'using' block will call DisposeAsync() on the transaction,
            // which automatically rolls back any uncommitted changes.
            _logger.LogError(ex, "[Transaction] Rolling back transaction for {RequestName}", requestName);
            throw;
        }
    }
}
