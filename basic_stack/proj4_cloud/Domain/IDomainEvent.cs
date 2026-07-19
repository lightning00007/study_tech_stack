namespace BookLibrary.CloudNative.Domain;

// =============================================================================
// DOMAIN/IDOMAIN EVENT.CS — The contract for domain events
// =============================================================================
// A domain event is a record of something significant that happened within the
// domain. It is a fact — it already happened. The name is always past tense:
// "BookPublished", "AuthorCreated", "BookArchived".
//
// Domain events are used to communicate changes to other parts of the system
// without creating direct dependencies. Instead of calling InventoryService
// directly, BookService raises a BookPublishedEvent and walks away.
// InventoryService (or any other interested party) subscribes to that event.
// =============================================================================

/// <summary>
/// Marker interface for all domain events in the Book Library.
/// Every domain event records something that happened in the past.
/// </summary>
public interface IDomainEvent
{
    /// <summary>Unique identifier for this specific event instance. Used for deduplication.</summary>
    Guid EventId { get; }

    /// <summary>When the event occurred (UTC). Used for ordering and debugging.</summary>
    DateTime OccurredAt { get; }
}
