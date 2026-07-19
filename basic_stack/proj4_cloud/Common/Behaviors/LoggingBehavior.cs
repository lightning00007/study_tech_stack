using System.Diagnostics;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BookLibrary.CloudNative.Common.Behaviors;

/// <summary>Logs every command/query name and execution duration.</summary>
public class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly ILogger<LoggingBehavior<TRequest, TResponse>> _logger;

    public LoggingBehavior(ILogger<LoggingBehavior<TRequest, TResponse>> logger) => _logger = logger;

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        var name = typeof(TRequest).Name;
        _logger.LogInformation("[START] {RequestName}", name);
        var sw = Stopwatch.StartNew();

        try
        {
            var response = await next();
            _logger.LogInformation("[END] {RequestName} in {Ms}ms", name, sw.ElapsedMilliseconds);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FAILED] {RequestName} after {Ms}ms", name, sw.ElapsedMilliseconds);
            throw;
        }
    }
}
