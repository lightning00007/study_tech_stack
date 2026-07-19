using System.Text.Json;
using BookLibrary.CloudNative.Domain;

namespace BookLibrary.CloudNative.Infrastructure.Persistence;

// =============================================================================
// INFRASTRUCTURE/PERSISTENCE/OUTBOXMESSAGE.CS — The Outbox Pattern
// =============================================================================
// PROBLEM STATEMENT:
// When a book is created, we want to:
//   1. Save the Book to PostgreSQL
//   2. Publish a BookCreatedEvent to AWS SNS
//
// The naive approach:
//   await db.SaveChangesAsync();
//   await sns.PublishAsync(bookCreatedEvent); // ← DANGER ZONE
//
// If the application crashes between step 1 and step 2:
//   - The book exists in PostgreSQL ✓
//   - The event was NEVER published ✗
//   - Email service never sends the confirmation email
//   - Search indexer never indexes the new book
//   - The system is silently inconsistent
//
// THE OUTBOX PATTERN SOLUTION:
// Instead of publishing directly to SNS, we save the event to an 'outbox_messages'
// table IN THE SAME DATABASE TRANSACTION as the business data:
//
//   BEGIN TRANSACTION
//     INSERT INTO books (...) VALUES (...)         ← business data
//     INSERT INTO outbox_messages (...) VALUES (...) ← event record
//   COMMIT TRANSACTION
//                │
//                │ (atomic — both succeed or both fail. No partial state.)
//                ▼
//   Background job reads unprocessed outbox messages
//   Background job publishes to SNS
//   Background job marks message as processed
//
// If the app crashes before the background job runs, the message is still in
// the database. When the app restarts, the background job will find and publish it.
// At-least-once delivery is guaranteed.
// =============================================================================

/// <summary>
/// Represents a domain event stored in the database, waiting to be published to SNS.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Full type name of the domain event (e.g., "BookCreatedEvent").</summary>
    public string EventType { get; init; } = string.Empty;

    /// <summary>JSON-serialised domain event payload. Stored as JSONB in PostgreSQL.</summary>
    public string Payload { get; init; } = string.Empty;

    /// <summary>When this message was created (same as the domain event's OccurredAt).</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// When this message was successfully published to SNS.
    /// NULL means it has not yet been published — the background job will pick it up.
    /// </summary>
    public DateTime? ProcessedAt { get; set; }

    /// <summary>If publishing failed, the error message is stored here for debugging.</summary>
    public string? Error { get; set; }

    /// <summary>Number of publish attempts. Used to detect and stop infinite retry loops.</summary>
    public int RetryCount { get; set; }

    // =========================================================================
    // Factory method: Convert a domain event to an OutboxMessage
    // =========================================================================

    /// <summary>
    /// Creates an OutboxMessage from a domain event.
    /// The event is serialised to JSON for durable storage.
    /// </summary>
    public static OutboxMessage FromDomainEvent(IDomainEvent domainEvent)
    {
        return new OutboxMessage
        {
            Id = Guid.NewGuid(),
            // Store the simple class name so the publisher can map it to an SNS topic ARN
            EventType = domainEvent.GetType().Name,
            // Serialise the CONCRETE type so the payload contains all event-specific fields
            // without losing any data to polymorphism
            Payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType()),
            CreatedAt = domainEvent.OccurredAt
        };
    }
}
