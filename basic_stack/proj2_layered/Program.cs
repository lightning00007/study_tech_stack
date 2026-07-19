using BookLibrary.Layered.Application;
using BookLibrary.Layered.Infrastructure;
using Microsoft.EntityFrameworkCore;

// =============================================================================
// PROGRAM.CS — Dependency Injection wiring
// =============================================================================
// The DI container is like a factory. You register your types once here,
// and ASP.NET Core creates them on demand and injects them where needed.
//
// The registration order matters for readability but NOT for correctness —
// the container resolves dependencies lazily when a type is first requested.
//
// Dependency graph for a POST /api/books request:
//
//   BooksController
//     └── IBookService  ──resolved as──►  BookService
//           ├── IBookRepository  ────────►  BookRepository
//           │     └── AppDbContext  ───────►  (configured below)
//           └── IAuthorRepository  ──────►  AuthorRepository
//                 └── AppDbContext  ───────►  (same instance, scoped!)
// =============================================================================

var builder = WebApplication.CreateBuilder(args);

// ── Database ──────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

// ── Repository registrations ─────────────────────────────────────────────────
// We register the INTERFACE → IMPLEMENTATION mapping.
// When code asks for IBookRepository, the container creates a BookRepository.
// Scoped means: one instance per HTTP request (shared within the request).
builder.Services.AddScoped<IBookRepository, BookRepository>();
builder.Services.AddScoped<IAuthorRepository, AuthorRepository>();

// ── Service registrations ─────────────────────────────────────────────────────
builder.Services.AddScoped<IBookService, BookService>();
builder.Services.AddScoped<IAuthorService, AuthorService>();

// ── ASP.NET Core infrastructure ───────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Book Library API — Level 2: Layered Architecture", Version = "v1" });
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

// Apply migrations on startup (fine for learning projects)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

app.UseRouting();
app.MapControllers();

app.Run();
