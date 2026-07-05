using FluentValidation;
using GrapeSeed.SharedKernel.Application;
using GrapeSeed.SharedKernel.Application.Behaviors;
using GrapeSeed.SharedKernel.Domain;
using GrapeSeed.TenantService.Domain;
using GrapeSeed.TenantService.Infrastructure.Payments;
using GrapeSeed.TenantService.Infrastructure.Persistence;
using MediatR;
using Microsoft.Extensions.Logging;

namespace GrapeSeed.TenantService.Application.Commands.RegisterTenant;

// =============================================================================
// 📖 CONCEPT: Command Object (CQRS)
// =============================================================================
// A Command is a message that expresses the *intent* to change state.
// It carries all the data needed to perform the operation.
//
// Using C# 'record' makes the command:
//   - Immutable (all properties are init-only by default for records)
//   - Self-documenting (all required data is visible in the constructor)
//   - Easily testable (value equality allows direct comparison in tests)
//
// The IRequest<Result<TenantId>> type parameter tells MediatR:
//   "When this command is sent, I expect a Result<TenantId> back."
//
// ITransactionalCommand tells TransactionBehavior to wrap this in a DB transaction.
// =============================================================================

/// <summary>
/// Command to register a new tenant on the GrapeSeed platform.
/// Triggers tenant creation, payment processing, and database schema provisioning.
/// </summary>
public sealed record RegisterTenantCommand(
    /// <summary>The school or company name displayed on the platform.</summary>
    string Name,

    /// <summary>Primary contact email. Used for billing notifications.</summary>
    string Email,

    /// <summary>
    /// The subscription plan identifier (e.g., "starter", "professional", "enterprise").
    /// Must match a plan in the Plans catalogue.
    /// </summary>
    string PlanId,

    /// <summary>
    /// The Stripe PaymentMethod ID obtained from the frontend (e.g., "pm_card_visa").
    /// The frontend collects card details via Stripe.js and exchanges them for this token.
    /// We NEVER receive raw card numbers — only the Stripe token.
    /// </summary>
    string StripePaymentMethodId

) : IRequest<Result<TenantId>>, ITransactionalCommand;

// =============================================================================
// 📖 CONCEPT: Command Validator (FluentValidation)
// =============================================================================
// Placed next to the command for easy discoverability.
// The ValidationBehavior automatically discovers and runs this validator.
// =============================================================================

/// <summary>Validates the RegisterTenantCommand before the handler runs.</summary>
public sealed class RegisterTenantCommandValidator : AbstractValidator<RegisterTenantCommand>
{
    private static readonly string[] ValidPlanIds = ["starter", "professional", "enterprise"];

    public RegisterTenantCommandValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Tenant name is required.")
            .MaximumLength(200).WithMessage("Tenant name cannot exceed 200 characters.");

        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("A valid email address is required.");

        RuleFor(x => x.PlanId)
            .NotEmpty().WithMessage("Plan ID is required.")
            .Must(id => ValidPlanIds.Contains(id))
            .WithMessage($"Plan ID must be one of: {string.Join(", ", ValidPlanIds)}");

        RuleFor(x => x.StripePaymentMethodId)
            .NotEmpty().WithMessage("A payment method is required.")
            .Must(id => id.StartsWith("pm_"))
            .WithMessage("Invalid Stripe payment method ID format.");
    }
}

// =============================================================================
// 📖 CONCEPT: Command Handler (MediatR IRequestHandler)
// =============================================================================
// The handler contains the business logic for processing the command.
// It is a pure C# class — no HTTP, no serialisation, no framework code.
// This makes it trivially testable with a mock repository and payment service.
//
// Orchestration flow:
//   1. Validate the plan exists (business rule check).
//   2. Create the Tenant domain object (raises TenantRegisteredEvent internally).
//   3. Process payment via Stripe.
//   4. Activate the tenant (raises TenantActivatedEvent internally).
//   5. Persist to the database (TransactionBehavior commits, dispatching outbox events).
// =============================================================================

/// <summary>
/// Handles the RegisterTenantCommand. Orchestrates tenant creation and payment.
/// </summary>
public sealed class RegisterTenantCommandHandler
    : IRequestHandler<RegisterTenantCommand, Result<TenantId>>
{
    private readonly ITenantRepository _repository;
    private readonly IStripePaymentService _paymentService;
    private readonly IPlanCatalogue _planCatalogue;
    private readonly ILogger<RegisterTenantCommandHandler> _logger;

    public RegisterTenantCommandHandler(
        ITenantRepository repository,
        IStripePaymentService paymentService,
        IPlanCatalogue planCatalogue,
        ILogger<RegisterTenantCommandHandler> logger)
    {
        _repository = repository;
        _paymentService = paymentService;
        _planCatalogue = planCatalogue;
        _logger = logger;
    }

    public async Task<Result<TenantId>> Handle(
        RegisterTenantCommand command,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Registering tenant: {TenantName} on plan {PlanId}",
            command.Name, command.PlanId);

        // ── Step 1: Resolve the subscription plan ──────────────────────────
        // The plan determines the monthly price. If the plan doesn't exist,
        // we fail early before touching payment or the database.
        var plan = await _planCatalogue.GetByIdAsync(command.PlanId, cancellationToken);
        if (plan is null)
            return Result<TenantId>.Failure($"Plan '{command.PlanId}' does not exist.");

        // ── Step 2: Check for duplicate email ─────────────────────────────
        var existingTenant = await _repository.GetByEmailAsync(command.Email, cancellationToken);
        if (existingTenant is not null)
            return Result<TenantId>.Failure($"An account with email '{command.Email}' already exists.");

        // ── Step 3: Create the Tenant domain object ────────────────────────
        // The static factory method validates invariants and raises TenantRegisteredEvent.
        var tenant = Tenant.Register(
            name: command.Name,
            email: new Email(command.Email),
            planId: command.PlanId,
            subscriptionFee: plan.MonthlyFee
        );

        // ── Step 4: Process payment via Stripe ────────────────────────────
        // 📖 CONCEPT: We call the payment service BEFORE saving to the database.
        // If payment fails, we want to return a clear error to the client
        // without creating a Pending tenant that might confuse the user.
        var paymentResult = await _paymentService.CreateSubscriptionAsync(
            email: command.Email,
            paymentMethodId: command.StripePaymentMethodId,
            planId: command.PlanId,
            cancellationToken: cancellationToken);

        if (paymentResult.IsFailure)
        {
            _logger.LogWarning("Payment failed for {Email}: {Error}", command.Email, paymentResult.Error);
            return Result<TenantId>.Failure($"Payment failed: {paymentResult.Error}");
        }

        // ── Step 5: Activate the tenant with the Stripe customer ID ────────
        // The Activate() method transitions the status Pending → Active
        // and raises TenantActivatedEvent.
        tenant.Activate(paymentResult.Value!.StripeCustomerId);

        // ── Step 6: Persist ────────────────────────────────────────────────
        // AddAsync stages the entity in EF Core's change tracker.
        // The actual INSERT runs when TransactionBehavior calls SaveChangesAsync(),
        // which also dispatches the domain events via the Outbox.
        await _repository.AddAsync(tenant, cancellationToken);

        _logger.LogInformation("Tenant {TenantId} registered and activated successfully.", tenant.Id);
        return Result<TenantId>.Success(tenant.Id);
    }
}

// =============================================================================
// Supporting interfaces (implemented in Infrastructure layer)
// =============================================================================

/// <summary>Repository for Tenant aggregate operations.</summary>
public interface ITenantRepository : IRepository<Tenant, TenantId>
{
    Task<Tenant?> GetByEmailAsync(string email, CancellationToken ct = default);
    Task<Tenant?> GetBySlugAsync(string slug, CancellationToken ct = default);
}

/// <summary>Catalogue of available subscription plans.</summary>
public interface IPlanCatalogue
{
    Task<Plan?> GetByIdAsync(string planId, CancellationToken ct = default);
}

/// <summary>A subscription plan with pricing information.</summary>
public sealed record Plan(string Id, string DisplayName, Money MonthlyFee, string[] Features);
