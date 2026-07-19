using BookLibrary.Layered.Domain;

namespace BookLibrary.Layered.Application;

// =============================================================================
// APPLICATION/IBOOKREPOSITORY.CS — The contract for data access
// =============================================================================
// This interface lives in the APPLICATION layer, not the Infrastructure layer.
// That might seem backwards — isn't the repository about data access?
//
// The key insight: the Application layer defines WHAT it needs (the interface),
// and the Infrastructure layer decides HOW to implement it.
//
// This is called Dependency Inversion:
//   Application layer ←── depends on ──── IBookRepository (interface)
//   Infrastructure layer ──── implements ──► IBookRepository (concrete class)
//
// The application layer doesn't know if IBookRepository uses EF Core, Dapper,
// an in-memory list, or reads from a file. It only knows the contract.
//
// This makes it trivial to swap the implementation in unit tests:
//   In production: inject BookRepository (talks to PostgreSQL)
//   In tests:      inject FakeBookRepository (uses List<Book>)
// =============================================================================

/// <summary>
/// Defines the data access contract for Book entities.
/// Implemented by BookRepository in the Infrastructure layer.
/// </summary>
public interface IBookRepository
{
    Task<Book?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<Book>> GetAllAsync(CancellationToken ct = default);
    Task<bool> IsbnExistsAsync(string isbn, CancellationToken ct = default);
    Task AddAsync(Book book, CancellationToken ct = default);
    void Update(Book book);
    void Delete(Book book);
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

/// <summary>
/// Defines the data access contract for Author entities.
/// </summary>
public interface IAuthorRepository
{
    Task<Author?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<Author>> GetAllAsync(CancellationToken ct = default);
    Task AddAsync(Author author, CancellationToken ct = default);
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
