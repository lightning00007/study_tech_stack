using BookLibrary.Monolith;
using Microsoft.EntityFrameworkCore;

// =============================================================================
// PROGRAM.CS — The entry point of the application
// =============================================================================
// In .NET 8 with Minimal API, Program.cs does three things:
//   1. Configure services (tell ASP.NET Core what it needs to build)
//   2. Build the application
//   3. Map HTTP endpoints (define what URL does what)
//
// At this level of complexity, we keep everything in this one file.
// Notice: there is NO repository pattern, NO service layer, NO interfaces.
// The database operations happen right here in the endpoint lambdas.
// This is a completely valid approach for small, simple applications.
// =============================================================================

var builder = WebApplication.CreateBuilder(args);

// ── Register EF Core with PostgreSQL ─────────────────────────────────────────
// We tell EF Core to use PostgreSQL (via Npgsql) and where to find the database.
// The connection string is read from appsettings.json.
//
// Why do we register DbContext here and not create it manually?
// Because ASP.NET Core's Dependency Injection (DI) container manages the lifecycle.
// Each HTTP request gets its own DbContext instance (Scoped lifetime), which is
// automatically disposed when the request ends. This prevents connection leaks.
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Book Library API — Level 1: Monolith", Version = "v1" });
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

// ── Apply migrations automatically on startup ─────────────────────────────────
// In a real production app you would NOT do this — migrations run as part of
// the deployment pipeline, not the application startup. However, for a learning
// project it makes the setup frictionless: just run the app and the database is ready.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

// =============================================================================
// HTTP ENDPOINTS
// =============================================================================
// Notice that the database logic is written DIRECTLY inside the endpoint
// handler lambdas. There is no middle layer — the HTTP handler IS the business
// logic. This is the "transaction script" pattern: each endpoint is a script
// that reads inputs, does work, and writes outputs.
//
// This approach is quick to write but hard to test and hard to maintain as the
// application grows. We'll fix this in Project 2.
// =============================================================================

// ── Authors ──────────────────────────────────────────────────────────────────

app.MapGet("/authors", async (AppDbContext db) =>
{
    var authors = await db.Authors
        .OrderBy(a => a.LastName)
        .Select(a => new AuthorResponse(a.Id, a.FirstName, a.LastName, a.Bio, a.BornYear))
        .ToListAsync();

    return Results.Ok(authors);
})
.WithName("GetAllAuthors")
.WithSummary("Get all authors");

app.MapGet("/authors/{id:int}", async (int id, AppDbContext db) =>
{
    var author = await db.Authors.FindAsync(id);

    return author is null
        ? Results.NotFound($"Author with ID {id} was not found.")
        : Results.Ok(new AuthorResponse(author.Id, author.FirstName, author.LastName, author.Bio, author.BornYear));
})
.WithName("GetAuthorById")
.WithSummary("Get a single author by ID");

app.MapPost("/authors", async (CreateAuthorRequest request, AppDbContext db) =>
{
    // Manual validation — no FluentValidation or data annotation validation middleware here.
    // If something is wrong, we return a 400 Bad Request manually.
    if (string.IsNullOrWhiteSpace(request.FirstName) || string.IsNullOrWhiteSpace(request.LastName))
        return Results.BadRequest("First name and last name are required.");

    var author = new Author
    {
        FirstName = request.FirstName.Trim(),
        LastName = request.LastName.Trim(),
        Bio = request.Bio,
        BornYear = request.BornYear
    };

    db.Authors.Add(author);
    await db.SaveChangesAsync();

    return Results.Created($"/authors/{author.Id}",
        new AuthorResponse(author.Id, author.FirstName, author.LastName, author.Bio, author.BornYear));
})
.WithName("CreateAuthor")
.WithSummary("Create a new author");

// ── Books ─────────────────────────────────────────────────────────────────────

app.MapGet("/books", async (AppDbContext db) =>
{
    // EF Core translates this LINQ query into SQL with a JOIN on the authors table.
    // .Include() tells EF Core to eagerly load the related Author entity.
    var books = await db.Books
        .Include(b => b.Author)
        .OrderBy(b => b.Title)
        .Select(b => new BookResponse(
            b.Id,
            b.Title,
            b.Isbn,
            $"{b.Author.FirstName} {b.Author.LastName}",
            b.PublishedYear,
            b.PageCount,
            b.CreatedAt))
        .ToListAsync();

    return Results.Ok(books);
})
.WithName("GetAllBooks")
.WithSummary("Get all books");

app.MapGet("/books/{id:int}", async (int id, AppDbContext db) =>
{
    var book = await db.Books
        .Include(b => b.Author)
        .FirstOrDefaultAsync(b => b.Id == id);

    if (book is null)
        return Results.NotFound($"Book with ID {id} was not found.");

    return Results.Ok(new BookResponse(
        book.Id,
        book.Title,
        book.Isbn,
        $"{book.Author.FirstName} {book.Author.LastName}",
        book.PublishedYear,
        book.PageCount,
        book.CreatedAt));
})
.WithName("GetBookById")
.WithSummary("Get a single book by ID");

app.MapPost("/books", async (CreateBookRequest request, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(request.Title))
        return Results.BadRequest("Title is required.");
    if (string.IsNullOrWhiteSpace(request.Isbn))
        return Results.BadRequest("ISBN is required.");

    // Check if the author exists — again, this logic is right here in the endpoint.
    var authorExists = await db.Authors.AnyAsync(a => a.Id == request.AuthorId);
    if (!authorExists)
        return Results.BadRequest($"Author with ID {request.AuthorId} does not exist.");

    var book = new Book
    {
        Title = request.Title.Trim(),
        Isbn = request.Isbn.Trim(),
        AuthorId = request.AuthorId,
        PublishedYear = request.PublishedYear,
        PageCount = request.PageCount,
        CreatedAt = DateTime.UtcNow
    };

    db.Books.Add(book);
    await db.SaveChangesAsync();

    // After save, EF Core has populated book.Id from the database.
    return Results.Created($"/books/{book.Id}", new { book.Id, book.Title, book.Isbn });
})
.WithName("CreateBook")
.WithSummary("Create a new book");

app.Run();
