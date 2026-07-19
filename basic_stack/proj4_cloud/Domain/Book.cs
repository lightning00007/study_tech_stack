namespace BookLibrary.CloudNative.Domain;

// =============================================================================
// DOMAIN/BOOK.CS — Aggregate Root with domain events
// =============================================================================
// This is Book's most evolved form. Compare with Project 1's Book:
//
// Project 1 (data bag):                Project 4 (aggregate root):
// ─────────────────────                ─────────────────────────────────────
// public class Book                    public sealed class Book : AggregateRoot<int>
// {                                    {
//   public string Title { get; set; }    public string Title { get; private set; }
//   (no business logic)                  public bool IsPublished { get; private set; }
// }                                      
//                                        public static Book Create(...) { ... }
//                                        public void Publish() { ... }
//                                        // Raises BookPublishedEvent internally
//                                      }
//
// The key additions in Project 4:
//   1. Inherits AggregateRoot<int> — signals it is the root of its cluster
//   2. Has a lifecycle: Created → Published
//   3. Raises domain events when lifecycle changes occur
//   4. Private setters prevent external mutation — state changes via methods only
// =============================================================================

/// <summary>
/// Book aggregate root. Manages the book lifecycle and raises domain events.
/// </summary>
public sealed class Book : AggregateRoot<int>
{
    public string Title { get; private set; }
    public string Isbn { get; private set; }
    public bool IsPublished { get; private set; }
    public int? PublishedYear { get; private set; }
    public int? PageCount { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public int AuthorId { get; private set; }
    public Author Author { get; private set; } = null!;

    // EF Core needs this to materialise objects from the database
    private Book()
    {
        Title = string.Empty;
        Isbn = string.Empty;
    }

    // ==========================================================================
    // 📖 CONCEPT: Static Factory Method
    // We use a factory method instead of a public constructor so that:
    //   1. The method name ('Create') expresses domain intent
    //   2. We can validate before the object exists
    //   3. We can raise a domain event at creation time
    // ==========================================================================

    /// <summary>
    /// Creates a new Book in the "draft" state and raises BookCreatedEvent.
    /// </summary>
    public static Book Create(string title, string isbn, Author author, int? publishedYear, int? pageCount)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Title cannot be empty.", nameof(title));
        if (string.IsNullOrWhiteSpace(isbn))
            throw new ArgumentException("ISBN cannot be empty.", nameof(isbn));

        var book = new Book
        {
            Title = title.Trim(),
            Isbn = isbn.Trim(),
            AuthorId = author.Id,
            Author = author,
            IsPublished = false,
            PublishedYear = publishedYear,
            PageCount = pageCount,
            CreatedAt = DateTime.UtcNow
        };

        // 📖 CONCEPT: Raising a domain event at creation time.
        // This event will be stored in the outbox table during SaveChanges()
        // and published to SNS by the OutboxPublisherJob background service.
        book.RaiseDomainEvent(new BookCreatedEvent(
            EventId: Guid.NewGuid(),
            OccurredAt: DateTime.UtcNow,
            BookId: book.Id, // Will be 0 until EF Core assigns the DB-generated ID
            Title: book.Title,
            Isbn: book.Isbn,
            AuthorId: author.Id,
            AuthorName: author.FullName
        ));

        return book;
    }

    /// <summary>
    /// Publishes the book, making it publicly visible.
    /// Raises BookPublishedEvent if not already published.
    /// </summary>
    public void Publish()
    {
        // Guard clause: enforce valid state transitions.
        // A book can only be published once.
        if (IsPublished)
            throw new InvalidOperationException($"Book '{Title}' is already published.");

        IsPublished = true;

        RaiseDomainEvent(new BookPublishedEvent(
            EventId: Guid.NewGuid(),
            OccurredAt: DateTime.UtcNow,
            BookId: Id,
            Title: Title,
            AuthorName: Author.FullName
        ));
    }
}

/// <summary>
/// Author entity. Not an aggregate root — it's a standalone entity.
/// </summary>
public sealed class Author : AggregateRoot<int>
{
    public string FirstName { get; private set; }
    public string LastName { get; private set; }
    public string? Bio { get; private set; }
    public int? BornYear { get; private set; }

    private readonly List<Book> _books = new();
    public IReadOnlyList<Book> Books => _books.AsReadOnly();

    private Author()
    {
        FirstName = string.Empty;
        LastName = string.Empty;
    }

    public static Author Create(string firstName, string lastName, string? bio, int? bornYear)
    {
        if (string.IsNullOrWhiteSpace(firstName))
            throw new ArgumentException("First name cannot be empty.");
        if (string.IsNullOrWhiteSpace(lastName))
            throw new ArgumentException("Last name cannot be empty.");

        return new Author
        {
            FirstName = firstName.Trim(),
            LastName = lastName.Trim(),
            Bio = bio,
            BornYear = bornYear
        };
    }

    public string FullName => $"{FirstName} {LastName}";
}
