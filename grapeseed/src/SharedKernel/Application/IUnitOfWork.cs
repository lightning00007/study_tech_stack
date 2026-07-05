using GrapeSeed.SharedKernel.Domain;

namespace GrapeSeed.SharedKernel.Application;

// =============================================================================
// 📖 CONCEPT: Unit of Work Pattern
// =============================================================================
// The Unit of Work (UoW) pattern tracks all changes made to domain objects
// during a business transaction and coordinates writing those changes out
// to the database in a single, atomic operation.
//
// Why not just call SaveChanges() directly on the DbContext?
//
//   1. Testability: Handlers can be tested with a mock IUnitOfWork that
//      doesn't touch a real database.
//   2. Abstraction: If we swap EF Core for another ORM, only the concrete
//      implementation changes — handlers remain untouched.
//   3. Domain event dispatch: Our UoW implementation dispatches domain events
//      AFTER committing to the database. This ensures events are only
//      raised for changes that were actually persisted.
//
// 🔗 SEE ALSO: TransactionBehavior.cs — wraps commands in a UoW transaction
// =============================================================================

/// <summary>
/// Coordinates database commits and domain event dispatch.
/// Implementations wrap EF Core's DbContext.SaveChangesAsync().
/// </summary>
public interface IUnitOfWork
{
    /// <summary>
    /// Commits all tracked changes to the database, then dispatches domain events.
    /// </summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

// =============================================================================
// 📖 CONCEPT: Repository Interface
// =============================================================================
// The Repository pattern provides an in-memory-like collection interface for
// accessing domain objects. Callers don't need to know whether the data
// comes from PostgreSQL, an in-memory store, or a cache.
// =============================================================================

/// <summary>
/// Generic repository contract for aggregate roots.
/// </summary>
/// <typeparam name="TAggregate">The aggregate root type.</typeparam>
/// <typeparam name="TId">The type of the aggregate's ID.</typeparam>
public interface IRepository<TAggregate, TId>
    where TAggregate : AggregateRoot<TId>
    where TId : notnull
{
    /// <summary>Returns the aggregate with the given ID, or null if not found.</summary>
    Task<TAggregate?> GetByIdAsync(TId id, CancellationToken ct = default);

    /// <summary>Adds a new aggregate to the change tracker (not yet saved).</summary>
    Task AddAsync(TAggregate aggregate, CancellationToken ct = default);

    /// <summary>Marks the aggregate as modified in the change tracker (not yet saved).</summary>
    void Update(TAggregate aggregate);

    /// <summary>Marks the aggregate for deletion (not yet saved).</summary>
    void Delete(TAggregate aggregate);
}
