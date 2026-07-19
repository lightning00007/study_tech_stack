namespace BookLibrary.CloudNative.Domain;

// =============================================================================
// DOMAIN/ENTITY.CS — The base class for all domain entities
// =============================================================================
// In Domain-Driven Design (DDD), an Entity is an object with a distinct
// identity that persists over time. Two entities are equal if they have the
// same ID — even if all their other properties are different.
//
// Example: A Student who changes their name is still the same Student.
//          Their identity (StudentId) is what makes them "the same person",
//          not the value of their name property.
//
// Compare this to Value Objects: two Money(100, "USD") instances are equal
// because they have the same values — Money has no identity.
//
// The generic TId parameter allows different entity types to have different
// ID types:
//   Entity<Guid>   — most entities (uses UUID in PostgreSQL for global uniqueness)
//   Entity<int>    — entities where an integer auto-increment ID is sufficient
//   Entity<string> — entities with natural keys (e.g., ISO country code)
// =============================================================================

/// <summary>
/// Base class for all domain entities.
/// An entity has identity and can raise domain events.
/// </summary>
public abstract class Entity<TId> where TId : notnull
{
    // Domain events are stored inside the entity until SaveChanges is called.
    // This is the core of the Outbox Pattern: the entity accumulates events
    // and the Unit of Work (DbContext) converts them to OutboxMessages.
    private readonly List<IDomainEvent> _domainEvents = new();

    /// <summary>The unique identity of this entity.</summary>
    public TId Id { get; protected set; } = default!;

    /// <summary>
    /// Domain events raised by this entity, waiting to be dispatched.
    /// Read by DbContext.SaveChangesAsync() to populate the OutboxMessages table.
    /// </summary>
    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    /// <summary>
    /// Raises a domain event. Called by the entity when something significant happens.
    /// Events are queued here and dispatched AFTER the database transaction commits.
    /// </summary>
    protected void RaiseDomainEvent(IDomainEvent domainEvent)
    {
        // 💡 WHY store events in the entity instead of publishing immediately?
        // If we published to SNS right here, and then SaveChanges() failed,
        // the event would be sent but no database change would have occurred.
        // The system would be inconsistent: subscribers think the book was published
        // but the database still shows it as a draft.
        //
        // By storing events in the entity and writing them to the outbox table
        // in the same transaction as the business data, we guarantee consistency.
        _domainEvents.Add(domainEvent);
    }

    /// <summary>Clears all queued events. Called by DbContext after converting to OutboxMessages.</summary>
    public void ClearDomainEvents() => _domainEvents.Clear();

    // Entities are equal if they have the same ID and are the same type.
    public override bool Equals(object? obj)
    {
        if (obj is not Entity<TId> other) return false;
        if (ReferenceEquals(this, other)) return true;
        if (GetType() != other.GetType()) return false;
        return Id.Equals(other.Id);
    }

    public override int GetHashCode() => Id.GetHashCode();
    public static bool operator ==(Entity<TId>? left, Entity<TId>? right) => Equals(left, right);
    public static bool operator !=(Entity<TId>? left, Entity<TId>? right) => !Equals(left, right);
}

/// <summary>
/// An Aggregate Root is the entry point for a cluster of related entities.
/// External code may only interact with the cluster through the aggregate root.
/// </summary>
public abstract class AggregateRoot<TId> : Entity<TId> where TId : notnull
{
    // Aggregate Roots add nothing mechanically to Entity — but they are a
    // semantic signal to the team: "This class is the root of an aggregate.
    // Never save child entities without going through this root."
    //
    // Example: BookChapter belongs to Book. You should NEVER save a Chapter
    // without going through Book.AddChapter() — because Book enforces the
    // invariant "a book cannot have more than 100 chapters."
}
