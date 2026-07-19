using BookLibrary.Cqrs.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BookLibrary.Cqrs.Infrastructure;

// Same Fluent API configuration as Project 2 — infrastructure doesn't change
// because of CQRS. The domain and data access contract are the same;
// only the APPLICATION layer organisation changed.

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Author> Authors => Set<Author>();
    public DbSet<Book> Books => Set<Book>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}

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
        builder.Property(b => b.PublishedYear).HasColumnName("published_year");
        builder.Property(b => b.PageCount).HasColumnName("page_count");
        builder.Property(b => b.CreatedAt).HasColumnName("created_at");
        builder.Property(b => b.AuthorId).HasColumnName("author_id");
    }
}
