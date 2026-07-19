using BookLibrary.Cqrs.Common;
using FluentValidation;
using MediatR;

namespace BookLibrary.Cqrs.Common.Behaviors;

// =============================================================================
// COMMON/BEHAVIORS/VALIDATIONBEHAVIOR.CS — Cross-cutting concern: validation
// =============================================================================
// This behaviour automatically validates every Command before the handler runs.
//
// WHAT IS FLUENTVALIDATION?
// FluentValidation is a library for writing validation rules in a fluent,
// readable style. Compare these approaches:
//
//   Data Annotations (Project 1):
//     [Required, MaxLength(200)]
//     public string Title { get; set; }
//     // Pro: Compact. Con: Limited to simple rules. On the model class itself.
//
//   Manual if/else (Project 1 & 2):
//     if (string.IsNullOrWhiteSpace(dto.Title))
//         return BadRequest("Title is required.");
//     // Pro: Full control. Con: Scattered across handlers/controllers.
//
//   FluentValidation (Project 3):
//     RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
//     // Pro: Centralised, testable, composable, very readable.
//
// HOW THIS BEHAVIOUR WORKS:
// 1. A Command arrives in the pipeline
// 2. ValidationBehavior looks for any IValidator<ThatCommand> registered in DI
// 3. If a validator exists, it runs ALL rules
// 4. If ANY rule fails, it returns a Failure Result immediately
// 5. The handler is never called if validation fails
//
// The handler code is clean — it can assume the input is already valid.
// =============================================================================

/// <summary>
/// Runs FluentValidation validators before the handler.
/// If validation fails, returns a Failure result without calling the handler.
/// </summary>
public class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IEnumerable<IValidator<TRequest>> _validators;

    public ValidationBehavior(IEnumerable<IValidator<TRequest>> validators)
    {
        _validators = validators;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        // If there are no validators registered for this request type, skip.
        if (!_validators.Any())
            return await next();

        // Run all validators and collect all failures
        var context = new ValidationContext<TRequest>(request);

        var failures = _validators
            .Select(v => v.Validate(context))
            .SelectMany(result => result.Errors)
            .Where(failure => failure is not null)
            .ToList();

        if (failures.Count == 0)
            return await next();

        // Build a combined error message from all validation failures
        var errorMessage = string.Join("; ", failures.Select(f => f.ErrorMessage));

        // We need to return a Failure Result without calling the handler.
        // Because TResponse can be anything (Result<Guid>, Result<BookDto>, etc.)
        // we use reflection to call Result<T>.Failure() on the right type.
        //
        // ⚠️ GOTCHA: This reflection-based approach is a known friction point.
        // In Project 4 (GrapeSeed's approach), we use a ValidationException
        // caught by an exception handling middleware instead.
        var responseType = typeof(TResponse);

        if (responseType == typeof(Result))
            return (TResponse)(object)Result.Failure(errorMessage);

        if (responseType.IsGenericType && responseType.GetGenericTypeDefinition() == typeof(Result<>))
        {
            var innerType = responseType.GetGenericArguments()[0];
            var failureMethod = responseType.GetMethod("Failure", [typeof(string)])!;
            return (TResponse)failureMethod.Invoke(null, [errorMessage])!;
        }

        // If the response type is not a Result, throw a ValidationException
        throw new ValidationException(failures);
    }
}
