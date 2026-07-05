using MediatR;
using Microsoft.Extensions.Logging;
using GrapeSeed.SharedKernel.Application;

namespace GrapeSeed.SharedKernel.Application.Behaviors;

// =============================================================================
// 📖 CONCEPT: MediatR Pipeline Behaviour — Transaction Management
// =============================================================================
// This behaviour wraps every Command (but NOT Queries) in a database transaction.
//
// Why only Commands?
//   Queries are read-only — they don't change state. Wrapping them in a transaction
//   adds unnecessary overhead (acquiring locks, managing transaction objects).
//
// How it detects Commands vs Queries:
//   We use a marker interface ITransactionalCommand. Any command that needs
//   database transaction wrapping implements this interface. Pure query requests
//   (IRequest<Result<SomeDto>>) do not implement it, and this behaviour skips them.
//
// What it does:
//   1. Begins a database transaction before the handler runs.
//   2. Commits the transaction if the handler completes successfully.
//   3. Rolls back the transaction if the handler throws an exception.
//
// This behaviour ensures that if a command handler makes multiple database changes
// (e.g., create Tenant + save Outbox message), they either all succeed or all fail.
// No partial commits.
// =============================================================================

/// <summary>
/// Marker interface for commands that require database transaction wrapping.
/// Queries should NOT implement this interface.
/// </summary>
public interface ITransactionalCommand { }

/// <summary>
/// Wraps transactional commands in a database transaction.
/// Commits on success, rolls back on exception.
/// </summary>
public sealed class TransactionBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<TransactionBehavior<TRequest, TResponse>> _logger;

    public TransactionBehavior(IUnitOfWork unitOfWork, ILogger<TransactionBehavior<TRequest, TResponse>> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        // 📖 CONCEPT: Duck typing via marker interface
        // Only commands marked with ITransactionalCommand go through this path.
        if (request is not ITransactionalCommand)
        {
            return await next();
        }

        var requestName = typeof(TRequest).Name;
        _logger.LogDebug("[Transaction] Beginning transaction for {RequestName}", requestName);

        try
        {
            // ⚠️ GOTCHA: The handler should NOT call SaveChangesAsync itself.
            // This behaviour calls it AFTER the handler returns, ensuring all
            // changes are committed together as one atomic unit.
            var response = await next();

            await _unitOfWork.SaveChangesAsync(cancellationToken);
            _logger.LogDebug("[Transaction] Committed transaction for {RequestName}", requestName);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Transaction] ROLLED BACK transaction for {RequestName}", requestName);
            // Re-throw so the global exception handler can convert it to an HTTP response
            throw;
        }
    }
}
