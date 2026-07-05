using MediatR;

namespace GrapeSeed.SharedKernel.Domain;

// =============================================================================
// 📖 CONCEPT: Domain Events
// =============================================================================
// A Domain Event is a record of something significant that happened within
// the domain. It is named in the past tense because it describes something
// that has already occurred.
//
// Examples:
//   TenantRegisteredEvent  — "A tenant was just registered"
//   PaymentProcessedEvent  — "A payment was just processed successfully"
//   VideoTranscodedEvent   — "A video was just transcoded and is ready to stream"
//
// Domain events serve two important purposes:
//
//   1. WITHIN the same aggregate/service: They allow other parts of the
//      domain model to react to changes without the entity needing to know
//      about all its observers (Publish/Subscribe within the domain).
//
//   2. ACROSS services: They are serialised and published to SNS, which
//      delivers them to other microservices via their SQS queues.
//
// 🔗 SEE ALSO: OutboxMessage.cs — how events are durably stored before publishing
// 🔗 SEE ALSO: SnsEventPublisher.cs — how events are sent to AWS SNS
// =============================================================================

/// <summary>
/// Marker interface for all domain events.
/// Inherits from MediatR's INotification so events can be dispatched within the process.
/// </summary>
public interface IDomainEvent : INotification
{
    /// <summary>Unique identifier for this event instance (for idempotency checks).</summary>
    Guid EventId { get; }

    /// <summary>UTC timestamp when the event occurred.</summary>
    DateTime OccurredAt { get; }
}

/// <summary>
/// Convenience base record for domain events.
/// Using 'record' ensures immutability and provides value equality for free.
/// </summary>
public abstract record DomainEvent : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
    public DateTime OccurredAt { get; } = DateTime.UtcNow;
}
