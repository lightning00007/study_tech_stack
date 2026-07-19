using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BookLibrary.Monolith;

// =============================================================================
// MODELS.CS — The raw data shapes for our application
// =============================================================================
// At this level of complexity, models serve a dual purpose:
//   1. They describe the C# objects our code works with.
//   2. They tell EF Core exactly how to create the database tables (via attributes).
//
// We are using Data Annotations — attributes like [Required], [MaxLength], [Key] —
// to configure both concerns in the same class. This is quick and easy for small
// applications, but becomes a problem as your app grows (more on this in GUIDE.md).
// =============================================================================

/// <summary>
/// Represents an author in the library system.
/// Each author can have many books.
/// </summary>
[Table("authors")]
public class Author
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Required]
    [MaxLength(100)]
    [Column("first_name")]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    [Column("last_name")]
    public string LastName { get; set; } = string.Empty;

    [MaxLength(500)]
    [Column("bio")]
    public string? Bio { get; set; }

    [Column("born_year")]
    public int? BornYear { get; set; }

    // Navigation property: one Author → many Books
    // EF Core uses this to understand the relationship.
    public List<Book> Books { get; set; } = new();
}

/// <summary>
/// Represents a book in the library system.
/// Each book belongs to one author.
/// </summary>
[Table("books")]
public class Book
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Required]
    [MaxLength(200)]
    [Column("title")]
    public string Title { get; set; } = string.Empty;

    [Required]
    [MaxLength(20)]
    [Column("isbn")]
    public string Isbn { get; set; } = string.Empty;

    [Column("published_year")]
    public int? PublishedYear { get; set; }

    [Column("page_count")]
    public int? PageCount { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Foreign key — EF Core will create a "author_id" column in the books table
    // and an index on it automatically because it ends in "Id" by convention.
    [Column("author_id")]
    public int AuthorId { get; set; }

    // Navigation property: each Book has one Author
    public Author Author { get; set; } = null!;
}

// =============================================================================
// DTOs — Data Transfer Objects
// =============================================================================
// We use separate DTO records for HTTP requests and responses.
// This prevents accidental exposure of internal model properties
// and gives us flexibility to evolve the API independently of the database schema.
// =============================================================================

/// <summary>Request body for creating a new author.</summary>
public record CreateAuthorRequest(string FirstName, string LastName, string? Bio, int? BornYear);

/// <summary>Request body for creating a new book.</summary>
public record CreateBookRequest(string Title, string Isbn, int AuthorId, int? PublishedYear, int? PageCount);

/// <summary>Response shape for an author.</summary>
public record AuthorResponse(int Id, string FirstName, string LastName, string? Bio, int? BornYear);

/// <summary>Response shape for a book (includes author name).</summary>
public record BookResponse(int Id, string Title, string Isbn, string AuthorName, int? PublishedYear, int? PageCount, DateTime CreatedAt);
