using BookLibrary.Layered.Domain;

namespace BookLibrary.Layered.Application;

// =============================================================================
// APPLICATION/BOOKSERVICE.CS — The Service Layer
// =============================================================================
// The service layer sits between the HTTP controllers and the data access layer.
// It is responsible for:
//   1. Orchestrating the steps needed to complete a use case
//   2. Enforcing business rules that span multiple entities
//   3. Providing a stable API that controllers depend on
//
// Notice what is NOT here:
//   - No HTTP concepts (HttpContext, IActionResult, etc.)
//   - No EF Core (no DbContext, no DbSet)
//   - Only pure C# and domain objects
//
// This makes the service layer independently testable: you can test
// BookService.CreateBookAsync() without starting a web server or a database.
// =============================================================================

// ── DTOs (Data Transfer Objects) ─────────────────────────────────────────────
// We define the shapes that flow between layers here in the Application layer.
// DTOs decouple the API shape from the domain model.

public record CreateBookDto(string Title, string Isbn, int AuthorId, int? PublishedYear, int? PageCount);
public record CreateAuthorDto(string FirstName, string LastName, string? Bio, int? BornYear);

public record BookDto(int Id, string Title, string Isbn, string AuthorName, int? PublishedYear, int? PageCount, DateTime CreatedAt);
public record AuthorDto(int Id, string FullName, string? Bio, int? BornYear, int BookCount);

// ── Service Result ────────────────────────────────────────────────────────────
// A simple wrapper to communicate success or failure without throwing exceptions.
// This is a precursor to the full Result<T> type we introduce in Project 3.
public record ServiceResult<T>(bool IsSuccess, T? Value, string? Error)
{
    public static ServiceResult<T> Success(T value) => new(true, value, null);
    public static ServiceResult<T> Fail(string error) => new(false, default, error);
}

// ── Book Service Interface ────────────────────────────────────────────────────

public interface IBookService
{
    Task<IReadOnlyList<BookDto>> GetAllBooksAsync(CancellationToken ct = default);
    Task<ServiceResult<BookDto>> GetBookByIdAsync(int id, CancellationToken ct = default);
    Task<ServiceResult<int>> CreateBookAsync(CreateBookDto dto, CancellationToken ct = default);
}

public interface IAuthorService
{
    Task<IReadOnlyList<AuthorDto>> GetAllAuthorsAsync(CancellationToken ct = default);
    Task<ServiceResult<AuthorDto>> GetAuthorByIdAsync(int id, CancellationToken ct = default);
    Task<ServiceResult<int>> CreateAuthorAsync(CreateAuthorDto dto, CancellationToken ct = default);
}

// ── Book Service Implementation ───────────────────────────────────────────────

/// <summary>
/// Implements book-related use cases by orchestrating the repository.
/// </summary>
public class BookService : IBookService
{
    private readonly IBookRepository _bookRepository;
    private readonly IAuthorRepository _authorRepository;

    // 💡 WHY constructor injection?
    // The IBookRepository is "injected" via the constructor. BookService doesn't
    // create the repository — it receives it from the outside. This is called
    // Dependency Injection (DI). The DI container (ASP.NET Core's built-in one)
    // creates BookService and passes in the right IBookRepository implementation.
    public BookService(IBookRepository bookRepository, IAuthorRepository authorRepository)
    {
        _bookRepository = bookRepository;
        _authorRepository = authorRepository;
    }

    public async Task<IReadOnlyList<BookDto>> GetAllBooksAsync(CancellationToken ct = default)
    {
        var books = await _bookRepository.GetAllAsync(ct);
        return books.Select(ToDto).ToList();
    }

    public async Task<ServiceResult<BookDto>> GetBookByIdAsync(int id, CancellationToken ct = default)
    {
        var book = await _bookRepository.GetByIdAsync(id, ct);
        if (book is null)
            return ServiceResult<BookDto>.Fail($"Book with ID {id} was not found.");

        return ServiceResult<BookDto>.Success(ToDto(book));
    }

    public async Task<ServiceResult<int>> CreateBookAsync(CreateBookDto dto, CancellationToken ct = default)
    {
        // Business rule 1: The author must exist before a book can be created.
        // This rule now lives HERE, not in the HTTP endpoint. If we add a
        // "bulk import books" feature, it can reuse this same service method.
        var author = await _authorRepository.GetByIdAsync(dto.AuthorId, ct);
        if (author is null)
            return ServiceResult<int>.Fail($"Author with ID {dto.AuthorId} does not exist.");

        // Business rule 2: ISBN must be unique across the library.
        var isbnTaken = await _bookRepository.IsbnExistsAsync(dto.Isbn, ct);
        if (isbnTaken)
            return ServiceResult<int>.Fail($"A book with ISBN '{dto.Isbn}' already exists.");

        // Delegate object creation to the domain class factory method.
        // The Book class enforces its own invariants (non-empty title, etc.)
        var book = Book.Create(dto.Title, dto.Isbn, author, dto.PublishedYear, dto.PageCount);

        await _bookRepository.AddAsync(book, ct);
        await _bookRepository.SaveChangesAsync(ct);

        return ServiceResult<int>.Success(book.Id);
    }

    // Private mapping helper: converts a domain Book to a BookDto.
    // This keeps the mapping logic in one place.
    private static BookDto ToDto(Book book) =>
        new(book.Id, book.Title, book.Isbn, book.Author.FullName, book.PublishedYear, book.PageCount, book.CreatedAt);
}

/// <summary>
/// Implements author-related use cases.
/// </summary>
public class AuthorService : IAuthorService
{
    private readonly IAuthorRepository _authorRepository;

    public AuthorService(IAuthorRepository authorRepository)
    {
        _authorRepository = authorRepository;
    }

    public async Task<IReadOnlyList<AuthorDto>> GetAllAuthorsAsync(CancellationToken ct = default)
    {
        var authors = await _authorRepository.GetAllAsync(ct);
        return authors.Select(a => new AuthorDto(a.Id, a.FullName, a.Bio, a.BornYear, a.Books.Count)).ToList();
    }

    public async Task<ServiceResult<AuthorDto>> GetAuthorByIdAsync(int id, CancellationToken ct = default)
    {
        var author = await _authorRepository.GetByIdAsync(id, ct);
        if (author is null)
            return ServiceResult<AuthorDto>.Fail($"Author with ID {id} was not found.");

        return ServiceResult<AuthorDto>.Success(new AuthorDto(author.Id, author.FullName, author.Bio, author.BornYear, author.Books.Count));
    }

    public async Task<ServiceResult<int>> CreateAuthorAsync(CreateAuthorDto dto, CancellationToken ct = default)
    {
        var author = Author.Create(dto.FirstName, dto.LastName, dto.Bio, dto.BornYear);
        await _authorRepository.AddAsync(author, ct);
        await _authorRepository.SaveChangesAsync(ct);
        return ServiceResult<int>.Success(author.Id);
    }
}
