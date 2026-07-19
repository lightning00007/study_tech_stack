using BookLibrary.Layered.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BookLibrary.Layered.Infrastructure;

// =============================================================================
// INFRASTRUCTURE/APPDBCONTEXT.CS — The Fluent API configuration approach
// =============================================================================
// Compare this with Project 1's AppDbContext. The biggest difference is that
// the mapping configuration has moved OUT of the domain classes and INTO
// separate IEntityTypeConfiguration<T> classes at the bottom of this file.
//
// This is called the Fluent API approach, and it is the RECOMMENDED approach
// for all but the simplest projects because:
//
//   1. Your domain classes stay clean — no database attributes.
//   2. Configuration is more powerful — Fluent API can express things that
//      Data Annotations cannot (e.g., filtered indexes, owned entities, etc.)
//   3. Configuration is in one predictable location — infrastructure layer.
// =============================================================================

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Author> Authors => Set<Author>();
    public DbSet<Book> Books => Set<Book>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // 💡 WHY ApplyConfigurationsFromAssembly?
        // This single call scans the current assembly for ALL classes that
        // implement IEntityTypeConfiguration<T> and applies them automatically.
        // You never need to manually register each configuration class — just
        // create a new class and it's picked up automatically.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}

// =============================================================================
// ENTITY TYPE CONFIGURATIONS — Separate mapping classes (Fluent API)
// =============================================================================

/// <summary>
/// Maps the Author domain class to the 'authors' PostgreSQL table.
/// Notice: the Author class itself has no database attributes.
/// </summary>
public class AuthorConfiguration : IEntityTypeConfiguration<Author>
{
    public void Configure(EntityTypeBuilder<Author> builder)
    {
        builder.ToTable("authors");

        // Primary key
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnName("id").UseIdentityColumn();

        // Properties
        builder.Property(a => a.FirstName)
            .HasColumnName("first_name")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(a => a.LastName)
            .HasColumnName("last_name")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(a => a.Bio)
            .HasColumnName("bio")
            .HasMaxLength(500);

        builder.Property(a => a.BornYear)
            .HasColumnName("born_year");

        // Relationship: one Author has many Books
        // HasMany/WithOne is how EF Core learns about 1-to-N relationships.
        builder.HasMany(a => a.Books)
            .WithOne(b => b.Author)
            .HasForeignKey(b => b.AuthorId)
            .OnDelete(DeleteBehavior.Restrict); // Don't cascade-delete books when author is deleted
    }
}

/// <summary>
/// Maps the Book domain class to the 'books' PostgreSQL table.
/// </summary>
public class BookConfiguration : IEntityTypeConfiguration<Book>
{
    public void Configure(EntityTypeBuilder<Book> builder)
    {
        builder.ToTable("books");

        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).HasColumnName("id").UseIdentityColumn();

        builder.Property(b => b.Title)
            .HasColumnName("title")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(b => b.Isbn)
            .HasColumnName("isbn")
            .HasMaxLength(20)
            .IsRequired();

        // A unique index on ISBN enforces the "ISBN must be unique" business rule
        // at the database level — a second line of defence after the service-layer check.
        builder.HasIndex(b => b.Isbn)
            .IsUnique()
            .HasDatabaseName("idx_books_isbn_unique");

        builder.Property(b => b.PublishedYear).HasColumnName("published_year");
        builder.Property(b => b.PageCount).HasColumnName("page_count");
        builder.Property(b => b.CreatedAt).HasColumnName("created_at");

        builder.Property(b => b.AuthorId).HasColumnName("author_id");
    }
}
