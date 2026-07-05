using GrapeSeed.SharedKernel.Application;
using MediatR;
using Microsoft.Extensions.Logging;
using Stripe;

namespace GrapeSeed.TenantService.Infrastructure.Payments;

// =============================================================================
// 📖 CONCEPT: Anti-Corruption Layer (ACL) for Stripe
// =============================================================================
// The IStripePaymentService interface (defined in the Application layer) uses
// GrapeSeed's own types (Result<PaymentResult>, Money, etc.).
// This implementation is the *only* place where Stripe-specific types and
// exceptions appear. If we ever switch from Stripe to Braintree, we only
// change this file — the rest of the application is unaffected.
//
// This pattern is called an Anti-Corruption Layer (ACL) in DDD:
// it prevents the external system's concepts from "leaking" into our domain.
//
// Security note on Stripe:
//   - We NEVER receive or store raw card numbers.
//   - The frontend uses Stripe.js to tokenise the card, producing a PaymentMethod ID (pm_xxx).
//   - We pass this token to Stripe's API, and Stripe handles PCI compliance.
// =============================================================================

/// <summary>Result of a successful Stripe payment/subscription creation.</summary>
public sealed record PaymentResult(
    string StripeCustomerId,
    string StripeSubscriptionId
);

/// <summary>
/// Abstracts Stripe payment operations. Defined in Application, implemented in Infrastructure.
/// </summary>
public interface IStripePaymentService
{
    Task<Result<PaymentResult>> CreateSubscriptionAsync(
        string email,
        string paymentMethodId,
        string planId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Stripe implementation of IStripePaymentService.
/// Uses Stripe.net SDK to create customers and subscriptions.
/// </summary>
public sealed class StripePaymentService : IStripePaymentService
{
    // 📖 CONCEPT: Stripe Price IDs map our plan IDs to Stripe's product/price catalogue.
    // These are configured in Stripe's dashboard and stored here as constants.
    // In production, these come from configuration (appsettings / AWS Parameter Store).
    private static readonly Dictionary<string, string> PlanToStripePriceId = new()
    {
        ["starter"]      = "price_starter_monthly",
        ["professional"] = "price_professional_monthly",
        ["enterprise"]   = "price_enterprise_monthly"
    };

    private readonly CustomerService _customerService;
    private readonly SubscriptionService _subscriptionService;
    private readonly PaymentMethodService _paymentMethodService;
    private readonly ILogger<StripePaymentService> _logger;

    public StripePaymentService(
        CustomerService customerService,
        SubscriptionService subscriptionService,
        PaymentMethodService paymentMethodService,
        ILogger<StripePaymentService> logger)
    {
        _customerService = customerService;
        _subscriptionService = subscriptionService;
        _paymentMethodService = paymentMethodService;
        _logger = logger;
    }

    public async Task<Result<PaymentResult>> CreateSubscriptionAsync(
        string email,
        string paymentMethodId,
        string planId,
        CancellationToken cancellationToken = default)
    {
        if (!PlanToStripePriceId.TryGetValue(planId, out var stripePriceId))
            return Result<PaymentResult>.Failure($"No Stripe price configured for plan '{planId}'.");

        try
        {
            // ── Step 1: Create a Stripe Customer ──────────────────────────
            // A Customer object in Stripe holds the billing email and payment methods.
            _logger.LogDebug("Creating Stripe customer for {Email}", email);
            var customer = await _customerService.CreateAsync(new CustomerCreateOptions
            {
                Email = email,
                Metadata = new Dictionary<string, string>
                {
                    // 📖 CONCEPT: Stripe metadata lets you attach your own data to Stripe objects.
                    // This makes it easy to correlate Stripe records with GrapeSeed records.
                    ["grapeseed_plan_id"] = planId
                }
            }, cancellationToken: cancellationToken);

            // ── Step 2: Attach the payment method to the customer ─────────
            await _paymentMethodService.AttachAsync(paymentMethodId, new PaymentMethodAttachOptions
            {
                Customer = customer.Id
            }, cancellationToken: cancellationToken);

            // ── Step 3: Set as default payment method ─────────────────────
            // ⚠️ GOTCHA: Creating a customer and attaching a payment method are separate
            // API calls. The payment method is NOT automatically set as the default.
            await _customerService.UpdateAsync(customer.Id, new CustomerUpdateOptions
            {
                InvoiceSettings = new CustomerInvoiceSettingsOptions
                {
                    DefaultPaymentMethod = paymentMethodId
                }
            }, cancellationToken: cancellationToken);

            // ── Step 4: Create the subscription ───────────────────────────
            // Stripe will immediately charge the customer for the first billing period.
            _logger.LogDebug("Creating Stripe subscription for customer {CustomerId} on price {PriceId}",
                customer.Id, stripePriceId);

            var subscription = await _subscriptionService.CreateAsync(new SubscriptionCreateOptions
            {
                Customer = customer.Id,
                Items = [new SubscriptionItemOptions { Price = stripePriceId }],
                // PaymentBehavior = "default_incomplete" means the subscription is created
                // but the payment is collected in the next step. We use "error_if_incomplete"
                // to fail fast if the payment method is declined.
                PaymentBehavior = "error_if_incomplete",
                Expand = ["latest_invoice.payment_intent"]
            }, cancellationToken: cancellationToken);

            if (subscription.Status != "active")
            {
                _logger.LogWarning("Stripe subscription created but not active. Status: {Status}", subscription.Status);
                return Result<PaymentResult>.Failure($"Payment was not completed. Subscription status: {subscription.Status}");
            }

            return Result<PaymentResult>.Success(new PaymentResult(
                StripeCustomerId: customer.Id,
                StripeSubscriptionId: subscription.Id
            ));
        }
        catch (StripeException ex)
        {
            // 📖 CONCEPT: Converting Stripe exceptions to our Result type (ACL boundary).
            // The handler never sees StripeException — it only sees Result<PaymentResult>.
            _logger.LogError(ex, "Stripe API error creating subscription for {Email}", email);
            return Result<PaymentResult>.Failure($"Payment processing error: {ex.StripeError?.Message ?? ex.Message}");
        }
    }
}
