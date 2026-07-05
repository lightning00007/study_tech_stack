namespace GrapeSeed.SharedKernel.Domain;

// =============================================================================
// 📖 CONCEPT: Entity Base Class
// =============================================================================
// In Domain-Driven Design, an Entity is an object that has a distinct identity
// that runs through time and different representations. Two entities are equal
// not because they have the same property values, but because they share the
// same identity (their ID).
//
// Example: Two Student objects with different names but the same StudentId
// represent the same student (perhaps before and after a name change).
//
// The generic type parameter TId allows entities to use different ID types:
// - Entity<Guid>    for most domain objects (UUID in PostgreSQL)
// - Entity<string>  for entities with natural keys (e.g., ISO country codes)
// =============================================================================

/// <summary>
/// Base class for all domain entities in GrapeSeed.
/// An entity has a stable identity and can hold domain events.
/// </summary>
/// <typeparam name="TId">The type of the entity's identifier.</typeparam>
public abstract class Entity<TId> where TId : notnull
{
    // 💡 WHY: We store domain events in the entity itself (not a separate bus)
    // so that they are only published after the entity is successfully saved.
    // This is the foundational piece of the Outbox pattern.
    private readonly List<IDomainEvent> _domainEvents = new();

    /// <summary>The unique identifier for this entity instance.</summary>
    public TId Id { get; protected init; } = default!;

    /// <summary>
    /// Raised domain events waiting to be dispatched after the unit of work commits.
    /// </summary>
    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    /// <summary>
    /// Adds a domain event to this entity's internal event list.
    /// Events are dispatched by the Unit of Work after SaveChanges.
    /// </summary>
    protected void RaiseDomainEvent(IDomainEvent domainEvent)
    {
        // 📖 CONCEPT: Domain events are raised by the entity, not by service code.
        // The entity knows best when something significant has happened in its lifecycle.
        _domainEvents.Add(domainEvent);
    }

    /// <summary>
    /// Clears all queued domain events. Called by the Unit of Work after dispatching.
    /// </summary>
    public void ClearDomainEvents() => _domainEvents.Clear();

    // =========================================================================
    // 📖 CONCEPT: Value-based equality for Entities is based solely on ID.
    // Two entity references with the same ID represent the same domain object.
    // =========================================================================
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
