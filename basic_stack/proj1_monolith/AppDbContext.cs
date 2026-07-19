using Microsoft.EntityFrameworkCore;

namespace BookLibrary.Monolith;

// =============================================================================
// APPDBCONTEXT.CS — The gateway between C# and the PostgreSQL database
// =============================================================================
// DbContext is the central class in Entity Framework Core. Think of it as:
//   - A shopping basket: it tracks all the objects you've loaded or created.
//   - A SQL generator: when you call SaveChanges(), it figures out what SQL
//     INSERT/UPDATE/DELETE statements are needed and runs them.
//   - A schema configurator: in OnModelCreating(), it reads your model
//     classes and builds the database schema.
//
// At this level (Project 1), we let EF Core read the Data Annotations
// from Models.cs to figure out the schema. This is called "convention-based"
// configuration — EF Core makes reasonable guesses based on naming conventions
// and attributes.
// =============================================================================

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    // DbSet<T> is the gateway to a specific database table.
    // Querying db.Books is equivalent to SELECT * FROM books.
    // Adding to db.Books stages an INSERT for the next SaveChanges() call.
    public DbSet<Author> Authors => Set<Author>();
    public DbSet<Book> Books => Set<Book>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // At this project level, EF Core reads our Data Annotations automatically.
        // We don't need to write anything here — conventions handle it.
        // But we still override this method to show where configuration lives.
        base.OnModelCreating(modelBuilder);
    }
}
