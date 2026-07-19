using BookLibrary.CloudNative.Domain;
using BookLibrary.CloudNative.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BookLibrary.CloudNative.Infrastructure.Persistence;

// =============================================================================
// INFRASTRUCTURE/PERSISTENCE/APPDBCONTEXT.CS — Outbox-aware DbContext
// =============================================================================
// This is the most evolved version of our DbContext. It overrides SaveChangesAsync
// to implement the Transactional Outbox Pattern automatically.
//
// The key difference from Project 3's DbContext:
// BEFORE committing the transaction, we scan all tracked entities for domain events.
// We convert those events to OutboxMessages and add them to the context.
// EVERYTHING — business data + outbox messages — commits in one atomic transaction.
//
// This means handlers NEVER manually interact with the outbox.
// They just call Book.Publish(), which raises an event.
// The DbContext intercepts SaveChanges and handles the rest.
// =============================================================================

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Author> Authors => Set<Author>();
    public DbSet<Book> Books => Set<Book>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }

    // ==========================================================================
    // 📖 CONCEPT: Intercepting SaveChanges for domain event collection
    // ==========================================================================
    // This override is the heart of the Outbox Pattern implementation.
    // It runs before every database commit and:
    //   1. Finds all entities that have raised domain events
    //   2. Converts each event to an OutboxMessage (JSON in a database row)
    //   3. Adds those OutboxMessages to the context
    //   4. Calls base.SaveChangesAsync() — commits EVERYTHING in one transaction
    // ==========================================================================
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        // Collect all entities with pending domain events
        // We look for Entity<int> because our entities use int IDs
        var entitiesWithEvents = ChangeTracker.Entries<Entity<int>>()
            .Where(entry => entry.Entity.DomainEvents.Any())
            .Select(entry => entry.Entity)
            .ToList();

        // Convert each domain event to an OutboxMessage
        foreach (var entity in entitiesWithEvents)
        {
            foreach (var domainEvent in entity.DomainEvents)
            {
                OutboxMessages.Add(OutboxMessage.FromDomainEvent(domainEvent));
            }
            // Clear the events from the entity — they've been persisted to the outbox
            entity.ClearDomainEvents();
        }

        // Everything commits together: business entities + outbox messages
        return await base.SaveChangesAsync(cancellationToken);
    }
}

// ── Entity Type Configurations ────────────────────────────────────────────────

public class AuthorConfiguration : IEntityTypeConfiguration<Author>
{
    public void Configure(EntityTypeBuilder<Author> builder)
    {
        builder.ToTable("authors");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnName("id").UseIdentityColumn();
        builder.Property(a => a.FirstName).HasColumnName("first_name").HasMaxLength(100).IsRequired();
        builder.Property(a => a.LastName).HasColumnName("last_name").HasMaxLength(100).IsRequired();
        builder.Property(a => a.Bio).HasColumnName("bio").HasMaxLength(500);
        builder.Property(a => a.BornYear).HasColumnName("born_year");
        builder.HasMany(a => a.Books).WithOne(b => b.Author).HasForeignKey(b => b.AuthorId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class BookConfiguration : IEntityTypeConfiguration<Book>
{
    public void Configure(EntityTypeBuilder<Book> builder)
    {
        builder.ToTable("books");
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).HasColumnName("id").UseIdentityColumn();
        builder.Property(b => b.Title).HasColumnName("title").HasMaxLength(200).IsRequired();
        builder.Property(b => b.Isbn).HasColumnName("isbn").HasMaxLength(20).IsRequired();
        builder.HasIndex(b => b.Isbn).IsUnique().HasDatabaseName("idx_books_isbn_unique");
        builder.Property(b => b.IsPublished).HasColumnName("is_published").HasDefaultValue(false);
        builder.Property(b => b.PublishedYear).HasColumnName("published_year");
        builder.Property(b => b.PageCount).HasColumnName("page_count");
        builder.Property(b => b.CreatedAt).HasColumnName("created_at");
        builder.Property(b => b.AuthorId).HasColumnName("author_id");
    }
}

public class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.EventType).HasMaxLength(500).IsRequired();

        // 📖 CONCEPT: JSONB column type in PostgreSQL
        // JSONB stores JSON in a binary format that PostgreSQL can index and query.
        // Unlike TEXT, JSONB can be queried with operators like ->> and @>.
        // Example: SELECT * FROM outbox_messages WHERE payload->>'BookId' = '42'
        builder.Property(o => o.Payload).HasColumnType("jsonb").IsRequired();

        builder.Property(o => o.CreatedAt).HasColumnName("created_at");
        builder.Property(o => o.ProcessedAt).HasColumnName("processed_at");
        builder.Property(o => o.Error).HasColumnName("error").HasMaxLength(2000);
        builder.Property(o => o.RetryCount).HasColumnName("retry_count").HasDefaultValue(0);

        // 📖 CONCEPT: Partial index
        // This index only covers rows where processed_at IS NULL (unprocessed messages).
        // When the OutboxPublisherJob queries for unprocessed messages, this index
        // makes that query O(log n) in the number of UNPROCESSED messages
        // (not the total messages). A table with millions of processed messages
        // but 10 pending ones will have a tiny index to scan.
        builder.HasIndex(o => o.CreatedAt)
            .HasFilter("processed_at IS NULL")
            .HasDatabaseName("idx_outbox_unprocessed");
    }
}
