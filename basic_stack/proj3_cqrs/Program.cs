using BookLibrary.Cqrs.Common.Behaviors;
using BookLibrary.Cqrs.Infrastructure;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

// =============================================================================
// PROGRAM.CS — Wiring MediatR, FluentValidation, and Pipeline Behaviours
// =============================================================================
// The key difference from Project 2: we no longer register individual services.
// Instead, MediatR scans the assembly and finds all handlers automatically.
// FluentValidation scans and finds all validators automatically.
// =============================================================================

var builder = WebApplication.CreateBuilder(args);

// ── Database ──────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

// ── MediatR ───────────────────────────────────────────────────────────────────
// RegisterServicesFromAssemblyContaining scans the entire assembly for:
//   - IRequestHandler<TRequest, TResponse> implementations (handlers)
//   - IPipelineBehavior<TRequest, TResponse> implementations (behaviours)
//
// Pipeline behaviours run in the order they are registered:
//   Logging → Validation → Handler
//
// Adding a new handler? Just create the class. No registration needed.
// Adding a new validator? Just create the class. No registration needed.
builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssemblyContaining<Program>();

    // Behaviours run in registration order — Logging wraps Validation wraps Handler
    cfg.AddOpenBehavior(typeof(LoggingBehavior<,>));
    cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
});

// ── FluentValidation ──────────────────────────────────────────────────────────
// Scans the assembly for AbstractValidator<T> implementations
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

// ── ASP.NET Core ──────────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Book Library API — Level 3: CQRS with MediatR", Version = "v1" });
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

app.UseRouting();
app.MapControllers();

app.Run();
