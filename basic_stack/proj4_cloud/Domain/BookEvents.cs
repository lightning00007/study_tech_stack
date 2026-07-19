namespace BookLibrary.CloudNative.Domain;

// =============================================================================
// DOMAIN/BOOKEVENTS.CS — Domain events for the Book aggregate
// =============================================================================
// Domain events are named with the aggregate root + past tense verb pattern.
// They carry enough information for subscribers to act without querying back.
//
// RULE: Domain events are IMMUTABLE. Once raised, they are a historical fact.
//       Use 'record' to enforce immutability and value equality.
// =============================================================================

/// <summary>
/// Raised when a new book is added to the library.
/// Subscribers: Notification service (send email to waiting list), Search indexer.
/// </summary>
public sealed record BookCreatedEvent(
    Guid EventId,
    DateTime OccurredAt,
    int BookId,
    string Title,
    string Isbn,
    int AuthorId,
    string AuthorName
) : IDomainEvent;

/// <summary>
/// Raised when a book is published (made publicly available).
/// Subscribers: Email newsletter service, Social media announcer.
/// </summary>
public sealed record BookPublishedEvent(
    Guid EventId,
    DateTime OccurredAt,
    int BookId,
    string Title,
    string AuthorName
) : IDomainEvent;
