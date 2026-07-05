using GrapeSeed.SharedKernel.Domain;
using GrapeSeed.TenantService.Domain.Events;

namespace GrapeSeed.TenantService.Domain;

// =============================================================================
// 📖 CONCEPT: Aggregate Root — Tenant
// =============================================================================
// Tenant is the central aggregate of the TenantService bounded context.
// It represents a school or training centre that has subscribed to GrapeSeed.
//
// As an AggregateRoot, Tenant:
//   - Has a unique identity (TenantId).
//   - Owns its business rules (e.g., "a Tenant can only be activated after payment").
//   - Raises domain events when significant state changes occur.
//   - Is the only entry point for external code — no direct access to inner objects.
//
// Design decisions:
//   - The constructor is private. The only way to create a Tenant is via the
//     static factory method Register(). This enforces invariants at creation time.
//   - Status transitions are modelled as methods (Activate, Suspend) rather than
//     direct property assignment. This prevents invalid state transitions.
// =============================================================================

/// <summary>
/// Strongly-typed ID for Tenant aggregate root.
/// Using a dedicated ID type prevents mixing up IDs from different aggregates
/// (e.g., accidentally passing a VideoId where a TenantId is expected).
/// </summary>
public sealed record TenantId(Guid Value)
{
    public static TenantId New() => new(Guid.NewGuid());
    public static TenantId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}

/// <summary>Represents the lifecycle status of a GrapeSeed tenant.</summary>
public enum TenantStatus
{
    /// <summary>Registration started but payment not yet confirmed.</summary>
    Pending,
    /// <summary>Payment confirmed; tenant has full platform access.</summary>
    Active,
    /// <summary>Tenant has been suspended (non-payment or policy violation).</summary>
    Suspended,
    /// <summary>Tenant has cancelled and data is pending deletion.</summary>
    Cancelled
}

/// <summary>
/// The Tenant aggregate root. Represents a subscribed school or training centre.
/// </summary>
public sealed class Tenant : AggregateRoot<TenantId>
{
    // 📖 CONCEPT: Properties use private setters (or init) to prevent
    // external code from modifying state directly. All mutations happen
    // through explicit business methods below.
    public string Name { get; private set; } = string.Empty;
    public Email Email { get; private set; } = null!;

    /// <summary>
    /// URL-friendly identifier. Used as the PostgreSQL schema name.
    /// Example: "Riverside High School" → slug "riverside-high-school" → schema "tenant_riverside_high_school"
    /// </summary>
    public string Slug { get; private set; } = string.Empty;

    public TenantStatus Status { get; private set; }
    public string PlanId { get; private set; } = string.Empty;

    /// <summary>Monthly subscription fee. Stored as a Value Object.</summary>
    public Money SubscriptionFee { get; private set; } = null!;

    /// <summary>Stripe's internal customer ID. Null until payment is set up.</summary>
    public string? StripeCustomerId { get; private set; }

    public DateTime CreatedAt { get; private init; }
    public DateTime? ActivatedAt { get; private set; }

    // EF Core requires a parameterless constructor for materialisation from database rows.
    // 💡 WHY private: prevents application code from creating incomplete Tenant instances.
    private Tenant() { }

    // =============================================================================
    // 📖 CONCEPT: Static Factory Method
    // =============================================================================
    // Instead of a public constructor, we use a static factory method named
    // Register(). This has several advantages:
    //
    //   1. The method name is expressive — it communicates domain intent.
    //   2. We can validate all inputs before the object is created.
    //   3. We can raise domain events at creation time.
    //   4. We can return a Result<Tenant> if creation can fail.
    // =============================================================================

    /// <summary>
    /// Registers a new tenant. Validates inputs and raises TenantRegisteredEvent.
    /// </summary>
    /// <param name="name">The school or company name.</param>
    /// <param name="email">The primary contact email address.</param>
    /// <param name="planId">The selected subscription plan identifier.</param>
    /// <param name="subscriptionFee">The monthly fee for the selected plan.</param>
    /// <returns>A new Tenant instance in Pending status.</returns>
    public static Tenant Register(
        string name,
        Email email,
        string planId,
        Money subscriptionFee)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tenant name cannot be empty.", nameof(name));
        if (string.IsNullOrWhiteSpace(planId))
            throw new ArgumentException("Plan ID cannot be empty.", nameof(planId));

        var tenant = new Tenant
        {
            Id = TenantId.New(),
            Name = name.Trim(),
            Email = email,
            Slug = GenerateSlug(name),
            PlanId = planId,
            SubscriptionFee = subscriptionFee,
            Status = TenantStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        // 📖 CONCEPT: Domain Event raised at creation time.
        // This event is stored in the entity's internal list and dispatched
        // by the Unit of Work after the database commit.
        tenant.RaiseDomainEvent(new TenantRegisteredEvent(
            tenant.Id,
            tenant.Name,
            tenant.Email.Value,
            tenant.Slug));

        return tenant;
    }

    /// <summary>
    /// Activates the tenant after successful payment confirmation.
    /// </summary>
    /// <param name="stripeCustomerId">The Stripe customer ID from the payment processor.</param>
    public void Activate(string stripeCustomerId)
    {
        // 📖 CONCEPT: Guard clause — enforce valid state transitions.
        // A tenant can only be activated from Pending status.
        // Attempting to activate an already-active tenant is a business rule violation.
        if (Status != TenantStatus.Pending)
            throw new InvalidOperationException(
                $"Cannot activate a tenant with status '{Status}'. Only Pending tenants can be activated.");

        StripeCustomerId = stripeCustomerId;
        Status = TenantStatus.Active;
        ActivatedAt = DateTime.UtcNow;

        RaiseDomainEvent(new TenantActivatedEvent(Id, StripeCustomerId));
    }

    /// <summary>Suspends the tenant (e.g., after payment failure).</summary>
    public void Suspend(string reason)
    {
        if (Status == TenantStatus.Cancelled)
            throw new InvalidOperationException("Cannot suspend a cancelled tenant.");

        Status = TenantStatus.Suspended;
        RaiseDomainEvent(new TenantSuspendedEvent(Id, reason));
    }

    /// <summary>
    /// Returns the PostgreSQL schema name for this tenant.
    /// The schema name is derived from the slug to be a valid PostgreSQL identifier.
    /// </summary>
    public string GetSchemaName() => $"tenant_{Slug.Replace("-", "_")}";

    // =========================================================================
    // Private helpers
    // =========================================================================

    private static string GenerateSlug(string name)
    {
        // Convert to lowercase, replace spaces and special chars with hyphens
        return name
            .ToLowerInvariant()
            .Replace(" ", "-")
            .Replace("'", "")
            .Replace(",", "")
            .Replace(".", "")
            .Trim('-');
    }
}
