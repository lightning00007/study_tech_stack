using System.Diagnostics;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BookLibrary.Cqrs.Common.Behaviors;

// =============================================================================
// COMMON/BEHAVIORS/LOGGINGBEHAVIOR.CS — Cross-cutting concern: logging
// =============================================================================
// This is a MediatR Pipeline Behaviour.
//
// WHAT IS A PIPELINE BEHAVIOUR?
// Think of middleware in ASP.NET Core — code that runs before and after every
// HTTP request. Pipeline Behaviours are the same concept, but for MediatR:
// code that wraps around every Command and Query.
//
// HOW DOES IT WORK?
// MediatR builds a chain (like Russian dolls):
//
//   LoggingBehavior
//     └── ValidationBehavior
//           └── Handler (your actual business logic)
//
// Each behaviour receives a 'next' delegate — calling next() passes control
// to the next item in the chain. Before next(): setup. After next(): teardown.
//
// WHY USE BEHAVIOURS INSTEAD OF WRITING LOGGING IN EVERY HANDLER?
// Without behaviours, you would write this in EVERY handler:
//
//   _logger.LogInformation("Handling CreateBookCommand...");
//   var stopwatch = Stopwatch.StartNew();
//   // ... actual logic ...
//   _logger.LogInformation("Done in {Ms}ms", stopwatch.ElapsedMilliseconds);
//
// With behaviours, you write it ONCE and it applies to every command and query
// automatically. This is the Open/Closed Principle: handlers are closed to
// modification, but open to new cross-cutting behaviours.
// =============================================================================

/// <summary>
/// Logs the name, duration, and outcome of every MediatR request.
/// Runs for ALL commands and queries — you don't need to add anything to handlers.
/// </summary>
public class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly ILogger<LoggingBehavior<TRequest, TResponse>> _logger;

    public LoggingBehavior(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    {
        _logger = logger;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var requestName = typeof(TRequest).Name;

        _logger.LogInformation("[START] Handling {RequestName}", requestName);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var response = await next(); // ← Call the next behaviour or the handler
            stopwatch.Stop();

            _logger.LogInformation("[END] {RequestName} handled in {ElapsedMs}ms",
                requestName, stopwatch.ElapsedMilliseconds);

            return response;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "[FAILED] {RequestName} failed after {ElapsedMs}ms",
                requestName, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }
}
