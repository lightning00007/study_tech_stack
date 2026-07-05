using System.Text.Json;
using GrapeSeed.SharedKernel.Domain;

namespace GrapeSeed.SharedKernel.Application.Messaging;

// =============================================================================
// 📖 CONCEPT: Outbox Message
// =============================================================================
// The OutboxMessage entity is the heart of the Transactional Outbox Pattern.
//
// The problem it solves:
//   After saving a Tenant to the database, we need to publish a TenantRegistered
//   event to SNS. But what if the application crashes between the database commit
//   and the SNS publish call? The tenant exists in the DB, but other services
//   never learn about it. The system is silently broken.
//
// The solution:
//   Save the event-to-be-published in the same database transaction as the business
//   entity. A separate background job (OutboxPublisher) reads unpublished messages
//   and delivers them to SNS. If SNS delivery fails, the job retries. If the app
//   crashes mid-delivery, the message remains in the outbox (ProcessedAt is still null)
//   and will be retried on restart.
//
// Guarantee: At-least-once delivery. The consumer must be idempotent.
//
// 🔗 SEE ALSO: docs/05-ef-core-and-mediatr.md#55-the-outbox-pattern
// 🔗 SEE ALSO: SnsEventPublisher.cs — the actual SNS publish implementation
// =============================================================================

/// <summary>
/// A durable record of a domain event that needs to be published to the message bus.
/// Stored in the database alongside business entities in the same transaction.
/// </summary>
public sealed class OutboxMessage
{
    /// <summary>Unique identifier for deduplication and tracking.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// The fully-qualified type name of the event (e.g., "GrapeSeed.TenantService.Domain.Events.TenantRegisteredEvent").
    /// Used by the consumer to deserialise back to the correct type.
    /// </summary>
    public string EventType { get; init; } = string.Empty;

    /// <summary>JSON-serialised event payload.</summary>
    public string Payload { get; init; } = string.Empty;

    /// <summary>UTC time the event was raised.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Set when the event was successfully published to SNS.
    /// Null means "not yet published".
    /// The OutboxPublisher background job queries WHERE ProcessedAt IS NULL.
    /// </summary>
    public DateTime? ProcessedAt { get; set; }

    /// <summary>
    /// Error message from the last failed publish attempt.
    /// Useful for diagnosing why a message is stuck in the outbox.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>Number of consecutive failed publish attempts.</summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// Factory method: serialises a domain event into an OutboxMessage.
    /// </summary>
    public static OutboxMessage FromDomainEvent(IDomainEvent domainEvent)
    {
        return new OutboxMessage
        {
            // 💡 WHY: We use AssemblyQualifiedName so the consumer can
            // call Type.GetType(EventType) to reconstruct the correct CLR type.
            EventType = domainEvent.GetType().AssemblyQualifiedName!,
            Payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType())
        };
    }
}

// =============================================================================
// 📖 CONCEPT: Event Publisher Interface
// =============================================================================
// The IEventPublisher interface abstracts the actual publishing mechanism.
// In production, this publishes to AWS SNS.
// In tests, a mock publisher captures events for assertion.
// =============================================================================

/// <summary>
/// Publishes domain events to the message bus (AWS SNS in production).
/// </summary>
public interface IEventPublisher
{
    /// <summary>
    /// Publishes a domain event to the message bus.
    /// For reliable delivery, always use the Outbox pattern (save event to DB first).
    /// </summary>
    Task PublishAsync(IDomainEvent domainEvent, CancellationToken ct = default);
}
