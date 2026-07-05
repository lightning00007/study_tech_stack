using FluentValidation;
using MediatR;

namespace GrapeSeed.SharedKernel.Application.Behaviors;

// =============================================================================
// 📖 CONCEPT: MediatR Pipeline Behaviour — Validation
// =============================================================================
// Before any command or query reaches its handler, this behaviour runs all
// registered FluentValidation validators for that request type.
//
// If validation fails, a ValidationException is thrown immediately. The handler
// never runs. This guarantees that handlers always receive valid input.
//
// How to use:
//   1. Create a validator class next to your command:
//      public class RegisterTenantCommandValidator : AbstractValidator<RegisterTenantCommand>
//      {
//          public RegisterTenantCommandValidator()
//          {
//              RuleFor(x => x.Email).NotEmpty().EmailAddress();
//              RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
//          }
//      }
//
//   2. Register it in the service's DI setup:
//      services.AddValidatorsFromAssemblyContaining<RegisterTenantCommand>();
//
//   3. That's it — the pipeline behaviour automatically discovers and runs validators.
//
// 💡 WHY FluentValidation over DataAnnotations?
//   - Rules are in code, not attributes — easier to test and compose.
//   - Supports complex cross-property rules: RuleFor(x => x.EndDate).GreaterThan(x => x.StartDate)
//   - Better error messages with conditional formatting.
// =============================================================================

/// <summary>
/// Validates incoming MediatR requests using FluentValidation.
/// Throws <see cref="ValidationException"/> if any validators report errors.
/// </summary>
public sealed class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IEnumerable<IValidator<TRequest>> _validators;

    public ValidationBehavior(IEnumerable<IValidator<TRequest>> validators)
    {
        // 📖 CONCEPT: IEnumerable<IValidator<TRequest>> is injected with ALL validators
        // registered for this request type. There can be multiple validators per request
        // (e.g., one for basic format validation, one for business rule validation).
        _validators = validators;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (!_validators.Any())
        {
            // No validators registered for this request — skip validation
            return await next();
        }

        var context = new ValidationContext<TRequest>(request);

        // Run all validators in parallel for efficiency
        var validationResults = await Task.WhenAll(
            _validators.Select(v => v.ValidateAsync(context, cancellationToken)));

        // Collect all failures across all validators
        var failures = validationResults
            .SelectMany(r => r.Errors)
            .Where(f => f is not null)
            .ToList();

        if (failures.Count > 0)
        {
            // ⚠️ GOTCHA: ValidationException is caught by the global exception handler
            // in Program.cs and converted to a 400 Bad Request response.
            // The handler never sees invalid input.
            throw new ValidationException(failures);
        }

        return await next();
    }
}
