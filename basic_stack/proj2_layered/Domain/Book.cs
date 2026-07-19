namespace BookLibrary.Layered.Domain;

// =============================================================================
// DOMAIN/BOOK.CS — A pure domain object with business logic
// =============================================================================
// Book is more interesting than Author because it contains a business rule:
// "A book cannot be created without a valid author."
//
// In Project 1, that rule lived inside the HTTP endpoint. Here, it lives inside
// the domain class itself. The Book class is responsible for ensuring it is
// always in a valid state — this is called "Encapsulation".
// =============================================================================

/// <summary>
/// Represents a book in the library.
/// </summary>
public class Book
{
    public int Id { get; private set; }
    public string Title { get; private set; }
    public string Isbn { get; private set; }
    public int? PublishedYear { get; private set; }
    public int? PageCount { get; private set; }
    public DateTime CreatedAt { get; private set; }

    // The foreign key — a simple integer pointing to the Author's ID.
    public int AuthorId { get; private set; }

    // Navigation property — EF Core populates this when you use .Include()
    public Author Author { get; private set; } = null!;

    // EF Core materialisation constructor — private to prevent misuse
    private Book()
    {
        Title = string.Empty;
        Isbn = string.Empty;
    }

    // 💡 WHY: We pass the entire Author object (not just the ID) to the factory.
    // This makes the business rule explicit: to create a Book, you MUST have a
    // valid Author object in hand. You can't "guess" an author ID and hope it works.
    public static Book Create(string title, string isbn, Author author, int? publishedYear, int? pageCount)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Title cannot be empty.", nameof(title));
        if (string.IsNullOrWhiteSpace(isbn))
            throw new ArgumentException("ISBN cannot be empty.", nameof(isbn));

        return new Book
        {
            Title = title.Trim(),
            Isbn = isbn.Trim(),
            AuthorId = author.Id,
            Author = author,
            PublishedYear = publishedYear,
            PageCount = pageCount,
            CreatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Updates the book's metadata.
    /// This is a domain method — it enforces that you can't set an empty title.
    /// </summary>
    public void UpdateDetails(string title, int? publishedYear, int? pageCount)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Title cannot be empty.", nameof(title));

        Title = title.Trim();
        PublishedYear = publishedYear;
        PageCount = pageCount;
    }
}
