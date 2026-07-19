using BookLibrary.Layered.Application;
using BookLibrary.Layered.Domain;
using Microsoft.EntityFrameworkCore;

namespace BookLibrary.Layered.Infrastructure;

// =============================================================================
// INFRASTRUCTURE/BOOKREPOSITORY.CS — The concrete data access implementation
// =============================================================================
// This class implements IBookRepository from the Application layer.
// It is the ONLY place in the entire application that knows about EF Core
// and how Books are stored in the database.
//
// The Application layer (BookService) only sees IBookRepository.
// It never imports EntityFrameworkCore. This strict layering means:
//   - BookService can be tested without a database
//   - You can swap EF Core for Dapper without changing BookService
// =============================================================================

public class BookRepository : IBookRepository
{
    private readonly AppDbContext _db;

    // The AppDbContext is injected — BookRepository doesn't create it.
    public BookRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task<Book?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        // .Include() loads the Author navigation property.
        // Without it, book.Author would be null (lazy loading is off by default).
        return await _db.Books
            .Include(b => b.Author)
            .FirstOrDefaultAsync(b => b.Id == id, ct);
    }

    public async Task<IReadOnlyList<Book>> GetAllAsync(CancellationToken ct = default)
    {
        return await _db.Books
            .Include(b => b.Author)
            .OrderBy(b => b.Title)
            .ToListAsync(ct);
    }

    public async Task<bool> IsbnExistsAsync(string isbn, CancellationToken ct = default)
    {
        return await _db.Books.AnyAsync(b => b.Isbn == isbn, ct);
    }

    public async Task AddAsync(Book book, CancellationToken ct = default)
    {
        // _db.Books.Add() stages the INSERT in EF Core's change tracker.
        // Nothing is written to the database until SaveChangesAsync() is called.
        await _db.Books.AddAsync(book, ct);
    }

    public void Update(Book book)
    {
        // EF Core already tracks the entity — just mark it as Modified.
        _db.Books.Update(book);
    }

    public void Delete(Book book)
    {
        _db.Books.Remove(book);
    }

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        return await _db.SaveChangesAsync(ct);
    }
}

public class AuthorRepository : IAuthorRepository
{
    private readonly AppDbContext _db;

    public AuthorRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task<Author?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        return await _db.Authors
            .Include(a => a.Books)
            .FirstOrDefaultAsync(a => a.Id == id, ct);
    }

    public async Task<IReadOnlyList<Author>> GetAllAsync(CancellationToken ct = default)
    {
        return await _db.Authors
            .Include(a => a.Books)
            .OrderBy(a => a.LastName)
            .ToListAsync(ct);
    }

    public async Task AddAsync(Author author, CancellationToken ct = default)
    {
        await _db.Authors.AddAsync(author, ct);
    }

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        return await _db.SaveChangesAsync(ct);
    }
}
