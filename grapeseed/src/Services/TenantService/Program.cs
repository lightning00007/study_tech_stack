using FluentValidation;
using GrapeSeed.SharedKernel.Application.Behaviors;
using GrapeSeed.SharedKernel.Infrastructure.MultiTenancy;
using GrapeSeed.TenantService.Infrastructure.Messaging;
using GrapeSeed.TenantService.Infrastructure.Payments;
using GrapeSeed.TenantService.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Stripe;

// =============================================================================
// 📖 CONCEPT: ASP.NET Core Minimal Program.cs
// =============================================================================
// In .NET 6+, the startup code lives in Program.cs without a Startup class.
// This file does two things:
//   1. Registers services in the Dependency Injection (DI) container.
//   2. Configures the HTTP request pipeline (middleware order matters!).
//
// The DI container is like a factory that creates objects on demand.
// When a controller asks for IMediator, the container creates a MediatR instance
// wired up with all the handlers and behaviours registered below.
//
// Why DI matters:
//   - In production, TenantController gets StripePaymentService.
//   - In tests, TenantController gets MockPaymentService (no real Stripe calls).
//   - The controller's code never changes — only the registration changes.
// =============================================================================

// ── Logging setup (Serilog) ─────────────────────────────────────────────────
// 📖 CONCEPT: Structured logging with Serilog
// Serilog replaces ASP.NET Core's default logger with one that supports
// structured (JSON) output. This is essential for CloudWatch Insights queries.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("logs/tenant-service-.log", rollingInterval: RollingInterval.Day)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Service", "TenantService")
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

// ── Services Registration ────────────────────────────────────────────────────

// MediatR: scans the assembly for all IRequestHandler implementations
builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssemblyContaining<Program>();

    // 📖 CONCEPT: Pipeline behaviour registration order matters.
    // They run in registration order: Logging → Validation → Transaction → Handler
    cfg.AddOpenBehavior(typeof(LoggingBehavior<,>));
    cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
    cfg.AddOpenBehavior(typeof(TransactionBehavior<,>));
});

// FluentValidation: auto-discovers validators next to commands
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

// EF Core with PostgreSQL
// 💡 WHY Npgsql: It's the official, high-performance PostgreSQL driver for .NET.
//    It supports PostgreSQL-specific features like JSONB, arrays, and search_path.
builder.Services.AddDbContext<TenantDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("Postgres"),
        npgsql => npgsql
            .MigrationsHistoryTable("__ef_migrations_history", "shared")
            .CommandTimeout(30)
    )
);

// 📖 CONCEPT: Scoped TenantContext
// One TenantContext instance per HTTP request.
// Populated by TenantMiddleware early in the pipeline.
builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

// Infrastructure services
builder.Services.AddScoped<IStripePaymentService, StripePaymentService>();
builder.Services.AddScoped<IEventPublisher, SnsEventPublisher>();

// Stripe SDK services
// 💡 WHY register individual Stripe services (not StripeClient directly)?
//    It makes each service independently mockable in tests.
StripeConfiguration.ApiKey = builder.Configuration["Stripe:SecretKey"]
    ?? throw new InvalidOperationException("Stripe:SecretKey configuration is required.");
builder.Services.AddScoped<CustomerService>();
builder.Services.AddScoped<SubscriptionService>();
builder.Services.AddScoped<PaymentMethodService>();

// AWS SDK
// 📖 CONCEPT: AmazonSNSClient picks up credentials from:
//   1. Environment variables (AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY)
//   2. AWS IAM Role (preferred in production on ECS/EC2 — no hardcoded keys!)
//   3. ~/.aws/credentials (local development)
builder.Services.AddAWSService<Amazon.SimpleNotificationService.IAmazonSimpleNotificationService>();

// Health checks (for Kubernetes liveness/readiness probes)
builder.Services.AddHealthChecks()
    .AddNpgsql(builder.Configuration.GetConnectionString("Postgres")!)
    .AddUrlGroup(new Uri(builder.Configuration["Aws:Sns:HealthCheckUrl"]!), "sns");

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// ── HTTP Pipeline (middleware order is critical!) ────────────────────────────
// 📖 CONCEPT: Middleware executes in the order registered here.
//
// Request flow:
//   1. Serilog request logging (captures all requests for debugging)
//   2. Exception handling (catches unhandled exceptions, returns RFC 7807)
//   3. HTTPS redirection
//   4. Routing (determines which controller/endpoint handles the request)
//   5. Authentication (validates the JWT token)
//   6. TenantMiddleware (extracts tenant from JWT, populates ITenantContext)
//   7. Authorization (checks permissions — must come AFTER auth)
//   8. Controllers (finally, the controller runs)

app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        context.Response.ContentType = "application/problem+json";
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Title = "An unexpected error occurred.",
            Status = StatusCodes.Status500InternalServerError
        });
    });
});

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthentication();

// 📖 CONCEPT: TenantMiddleware runs AFTER authentication so we can trust
// the JWT claims to identify the tenant.
app.UseMiddleware<TenantMiddleware>();

app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
