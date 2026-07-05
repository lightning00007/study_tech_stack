using GrapeSeed.SharedKernel.Application.Messaging;
using GrapeSeed.SharedKernel.Domain;
using GrapeSeed.SharedKernel.Infrastructure.MultiTenancy;
using GrapeSeed.TenantService.Application.Commands.RegisterTenant;
using GrapeSeed.TenantService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GrapeSeed.TenantService.Infrastructure.Persistence;

// =============================================================================
// 📖 CONCEPT: DbContext in EF Core
// =============================================================================
// DbContext is EF Core's "Unit of Work" and "Repository" all in one. It:
//   - Tracks changes to entities (change tracking).
//   - Maps C# classes to database tables (via OnModelCreating).
//   - Executes SQL queries and commands.
//   - Manages database connections and transactions.
//
// In TenantService, we have ONE DbContext that manages:
//   - Tenants (in the shared schema) — the global registry
//   - OutboxMessages (in the shared schema) — for reliable event delivery
//
// Each tenant-specific service (VideoService, IdentityService) has its own
// DbContext that targets the tenant's schema (set dynamically per request).
// =============================================================================

/// <summary>
/// EF Core DbContext for the TenantService.
/// Manages the shared/global schema: tenants, plans, outbox messages.
/// </summary>
public sealed class TenantDbContext : DbContext, IUnitOfWork
{
    private readonly IMediator? _mediator; // Null in migration CLI context

    public TenantDbContext(DbContextOptions<TenantDbContext> options, IMediator? mediator = null)
        : base(options)
    {
        _mediator = mediator;
    }

    // EF Core DbSets act as gateways to database tables.
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // 📖 CONCEPT: Apply all IEntityTypeConfiguration classes from this assembly.
        // Each entity gets its own configuration class for a clean separation.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TenantDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }

    // =============================================================================
    // 📖 CONCEPT: Overriding SaveChangesAsync to dispatch domain events
    // =============================================================================
    // Before saving, we collect all domain events from all tracked aggregates.
    // We save them as OutboxMessages in the same transaction.
    // After the transaction commits, a background job reads the outbox and
    // publishes the events to SNS.
    //
    // This is the Transactional Outbox Pattern — it guarantees that we never
    // commit a business change without also recording the resulting event.
    // =============================================================================
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        // Collect all domain events from tracked aggregates
        var aggregatesWithEvents = ChangeTracker.Entries<Entity<TenantId>>()
            .Where(e => e.Entity.DomainEvents.Any())
            .Select(e => e.Entity)
            .ToList();

        // Convert each domain event to an OutboxMessage and add to the context
        foreach (var aggregate in aggregatesWithEvents)
        {
            foreach (var domainEvent in aggregate.DomainEvents)
            {
                OutboxMessages.Add(OutboxMessage.FromDomainEvent(domainEvent));
            }
            aggregate.ClearDomainEvents();
        }

        // Everything (business entities + outbox messages) commits in one transaction
        return await base.SaveChangesAsync(cancellationToken);
    }
}

// =============================================================================
// 📖 CONCEPT: Entity Type Configuration (EF Core)
// =============================================================================
// Separating the database mapping from the domain class keeps the domain clean.
// The Tenant class has no EF Core attributes — it's a pure domain object.
// All mapping decisions (column names, constraints, indexes) live here.
// =============================================================================

/// <summary>EF Core mapping configuration for the Tenant aggregate root.</summary>
public sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants");

        // 📖 CONCEPT: Strongly-typed ID conversion
        // TenantId is a value object wrapping a Guid. EF Core doesn't know how to
        // store TenantId directly, so we provide a converter: TenantId ↔ Guid.
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id)
            .HasConversion(id => id.Value, value => TenantId.From(value))
            .HasColumnName("id");

        builder.Property(t => t.Name)
            .HasColumnName("name")
            .HasMaxLength(200)
            .IsRequired();

        // 📖 CONCEPT: Value Object mapping via Owned Entity
        // Email is a Value Object, not an entity. EF Core's OwnsOne maps it
        // to a column in the same table rather than a separate table.
        builder.OwnsOne(t => t.Email, email =>
        {
            email.Property(e => e.Value)
                .HasColumnName("email")
                .HasMaxLength(255)
                .IsRequired();
            email.HasIndex(e => e.Value).IsUnique();
        });

        builder.Property(t => t.Slug)
            .HasColumnName("slug")
            .HasMaxLength(100)
            .IsRequired();
        builder.HasIndex(t => t.Slug).IsUnique();

        // 📖 CONCEPT: Enum stored as string for readability in the database.
        // Storing as int is more compact but makes debugging much harder
        // ("status = 1" vs "status = 'Active'").
        builder.Property(t => t.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(t => t.PlanId).HasColumnName("plan_id").HasMaxLength(50);
        builder.Property(t => t.StripeCustomerId).HasColumnName("stripe_customer_id").HasMaxLength(100);
        builder.Property(t => t.CreatedAt).HasColumnName("created_at");
        builder.Property(t => t.ActivatedAt).HasColumnName("activated_at");

        // 📖 CONCEPT: Owned entity for Money Value Object
        builder.OwnsOne(t => t.SubscriptionFee, money =>
        {
            money.Property(m => m.Amount).HasColumnName("subscription_fee_amount").HasPrecision(10, 2);
            money.Property(m => m.Currency).HasColumnName("subscription_fee_currency").HasMaxLength(3);
        });

        // 📖 CONCEPT: Optimistic concurrency with RowVersion
        // PostgreSQL uses xmin (system column) for this, or we can use a manual uint Version.
        // EF Core will automatically include Version in UPDATE WHERE clauses.
        builder.Property(t => t.Version).IsRowVersion();
    }
}

/// <summary>EF Core mapping configuration for OutboxMessage.</summary>
public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");
        builder.HasKey(o => o.Id);

        builder.Property(o => o.EventType).HasMaxLength(500).IsRequired();
        builder.Property(o => o.Payload).HasColumnType("jsonb").IsRequired();

        // 📖 CONCEPT: Partial index (see Chapter 6)
        // This index only covers rows where ProcessedAt IS NULL.
        // The Outbox publisher queries WHERE ProcessedAt IS NULL — this index makes it fast.
        builder.HasIndex(o => o.CreatedAt)
            .HasFilter("processed_at IS NULL")
            .HasDatabaseName("idx_outbox_unprocessed");
    }
}
