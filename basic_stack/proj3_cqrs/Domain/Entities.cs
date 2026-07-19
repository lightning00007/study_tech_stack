namespace BookLibrary.Cqrs.Domain;

// Domain classes for Project 3 are identical to Project 2 — pure domain objects.
// The domain doesn't change when we change our architectural patterns.

public class Author
{
    public int Id { get; private set; }
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

public class Book
{
    public int Id { get; private set; }
    public string Title { get; private set; }
    public string Isbn { get; private set; }
    public int? PublishedYear { get; private set; }
    public int? PageCount { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public int AuthorId { get; private set; }
    public Author Author { get; private set; } = null!;

    private Book()
    {
        Title = string.Empty;
        Isbn = string.Empty;
    }

    public static Book Create(string title, string isbn, Author author, int? publishedYear, int? pageCount)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Title cannot be empty.");
        if (string.IsNullOrWhiteSpace(isbn))
            throw new ArgumentException("ISBN cannot be empty.");

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
}
