using System.Diagnostics;
using MediatR;
using Microsoft.Extensions.Logging;

namespace GrapeSeed.SharedKernel.Application.Behaviors;

// =============================================================================
// 📖 CONCEPT: MediatR Pipeline Behaviour — Logging
// =============================================================================
// This behaviour automatically logs the start and end of every MediatR request.
// It measures execution time and logs a warning if a request is suspiciously slow.
//
// By registering this behaviour once in DI (AddTransient<IPipelineBehavior<,>...>),
// every command and query gets automatic logging without any code in the handler.
//
// This demonstrates the Open/Closed Principle:
//   - Open for extension: add new behaviours without changing handlers.
//   - Closed for modification: handlers never need to change to get logging.
// =============================================================================

/// <summary>
/// Logs every MediatR request with execution duration.
/// Emits a warning if the request exceeds the slow-request threshold.
/// </summary>
public sealed class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private const int SlowRequestThresholdMs = 500;

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

        // 📖 CONCEPT: Structured logging
        // We log the request *type name*, not the request *contents*.
        // Request contents might contain PII (passwords, emails) — never log those.
        _logger.LogInformation("[MediatR] Handling {RequestName}", requestName);

        var stopwatch = Stopwatch.StartNew();
        TResponse response;

        try
        {
            response = await next();
        }
        finally
        {
            stopwatch.Stop();

            if (stopwatch.ElapsedMilliseconds > SlowRequestThresholdMs)
            {
                // ⚠️ GOTCHA: Slow requests in synchronous handlers block the thread pool.
                // This warning is the first signal to investigate N+1 queries or missing indexes.
                _logger.LogWarning(
                    "[MediatR] SLOW REQUEST {RequestName} completed in {ElapsedMs}ms (threshold: {ThresholdMs}ms)",
                    requestName,
                    stopwatch.ElapsedMilliseconds,
                    SlowRequestThresholdMs);
            }
            else
            {
                _logger.LogInformation(
                    "[MediatR] Handled {RequestName} in {ElapsedMs}ms",
                    requestName,
                    stopwatch.ElapsedMilliseconds);
            }
        }

        return response;
    }
}
