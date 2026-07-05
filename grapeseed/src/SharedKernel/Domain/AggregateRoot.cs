namespace GrapeSeed.SharedKernel.Domain;

// =============================================================================
// 📖 CONCEPT: Aggregate Root
// =============================================================================
// An Aggregate is a cluster of domain objects (entities and value objects) that
// are treated as a single unit for data changes. The Aggregate Root is the
// "gateway" to the aggregate — the only entity that outside code is allowed to
// hold a reference to.
//
// Rules:
//   1. External code can only reference the Aggregate Root, not inner entities.
//   2. All invariants (business rules) within the aggregate are maintained by
//      the root.
//   3. Transactions should not span aggregate boundaries.
//
// In GrapeSeed:
//   - Tenant is an Aggregate Root. TenantAddress is an inner value object.
//     External code holds a reference to Tenant, not to TenantAddress.
//   - Video is an Aggregate Root. VideoQualityLevel is an inner value object.
// =============================================================================

/// <summary>
/// Marker base class for Aggregate Roots.
/// Aggregate Roots are the sole entry points to their aggregate clusters.
/// </summary>
public abstract class AggregateRoot<TId> : Entity<TId> where TId : notnull
{
    // 📖 CONCEPT: Aggregate version for optimistic concurrency control.
    //
    // When two concurrent requests both read a Tenant, modify it, and try to
    // save it, the second save should fail rather than silently overwriting
    // the first save's changes. The Version column is incremented on every
    // update. EF Core's RowVersion / ConcurrencyToken feature uses this to
    // detect conflicts.
    //
    // On conflict, a DbUpdateConcurrencyException is thrown. The application
    // can retry (re-read and re-apply the change) or return a 409 Conflict to the client.
    public uint Version { get; protected set; }
}
