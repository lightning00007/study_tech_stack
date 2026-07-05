using GrapeSeed.SharedKernel.Domain;
using GrapeSeed.TenantService.Domain;

namespace GrapeSeed.TenantService.Domain.Events;

// =============================================================================
// 📖 CONCEPT: Domain Events for the Tenant Lifecycle
// =============================================================================
// These events are published via the Outbox Pattern and flow through SNS/SQS
// to notify other microservices of changes in the Tenant lifecycle.
//
// Event naming convention (past tense):
//   TenantRegisteredEvent  — something that happened
//   NOT: TenantRegisterEvent (present/future — wrong tense)
//   NOT: OnTenantRegistered  (the "On" prefix is for event handlers, not events)
//
// All events are immutable records. Once created, they cannot be changed.
// This is important because events represent facts about the past.
// =============================================================================

/// <summary>
/// Published when a new tenant completes initial registration (before payment).
/// Consumers: IdentityService (prepare auth tables), VideoService (prepare media storage).
/// </summary>
public sealed record TenantRegisteredEvent(
    TenantId TenantId,
    string TenantName,
    string Email,
    string Slug
) : DomainEvent;

/// <summary>
/// Published when a tenant's payment is confirmed and the account is activated.
/// Consumers: NotificationService (send welcome email), BillingService (start billing cycle).
/// </summary>
public sealed record TenantActivatedEvent(
    TenantId TenantId,
    string StripeCustomerId
) : DomainEvent;

/// <summary>
/// Published when a tenant is suspended (typically due to payment failure).
/// Consumers: IdentityService (block student logins), NotificationService (send warning email).
/// </summary>
public sealed record TenantSuspendedEvent(
    TenantId TenantId,
    string Reason
) : DomainEvent;
