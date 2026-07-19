namespace BookLibrary.Layered.Domain;

// =============================================================================
// DOMAIN/AUTHOR.CS — A pure domain object
// =============================================================================
// Compare this with Project 1's Author class. Notice what is MISSING:
//   - No [Table("authors")] attribute
//   - No [Column("first_name")] attribute
//   - No [MaxLength(100)] attribute
//   - No [Key] attribute
//
// This class knows NOTHING about databases. It only knows about the business
// concept of an Author. This is called a "Plain Old C# Object" (POCO).
//
// The EF Core mapping (table name, column names, constraints) has been moved
// to a separate class: AuthorConfiguration in the Infrastructure layer.
//
// WHY does this matter?
//   1. You can create and test Author objects without a database.
//   2. If you switch from PostgreSQL to MongoDB, this class doesn't change.
//   3. The domain model can evolve independently of the database schema.
// =============================================================================

/// <summary>
/// Represents an author in the library.
/// This is a pure domain object — no database attributes, no framework dependencies.
/// </summary>
public class Author
{
    // Properties with private setters enforce that state can only change
    // through explicit methods — not by external code directly setting values.
    public int Id { get; private set; }
    public string FirstName { get; private set; }
    public string LastName { get; private set; }
    public string? Bio { get; private set; }
    public int? BornYear { get; private set; }

    // Navigation property: EF Core uses this to understand the relationship.
    // It is private so external code can't accidentally replace the collection.
    private readonly List<Book> _books = new();
    public IReadOnlyList<Book> Books => _books.AsReadOnly();

    // EF Core requires a parameterless constructor to materialise objects from database rows.
    // We make it private so application code MUST use the factory method below.
    private Author() 
    {
        FirstName = string.Empty;
        LastName = string.Empty;
    }

    // 💡 WHY a static factory method instead of a public constructor?
    // The factory method name 'Create' communicates domain intent.
    // It also lets us validate before the object exists — a constructor that throws
    // is valid but less readable than a factory method that clearly says "this can fail".
    public static Author Create(string firstName, string lastName, string? bio, int? bornYear)
    {
        if (string.IsNullOrWhiteSpace(firstName))
            throw new ArgumentException("First name cannot be empty.", nameof(firstName));
        if (string.IsNullOrWhiteSpace(lastName))
            throw new ArgumentException("Last name cannot be empty.", nameof(lastName));

        return new Author
        {
            FirstName = firstName.Trim(),
            LastName = lastName.Trim(),
            Bio = bio,
            BornYear = bornYear
        };
    }

    /// <summary>Updates the author's biographical information.</summary>
    public void UpdateBio(string? bio)
    {
        Bio = bio;
    }

    /// <summary>Returns the author's full name.</summary>
    public string FullName => $"{FirstName} {LastName}";
}
