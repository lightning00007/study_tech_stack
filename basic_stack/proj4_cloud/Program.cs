using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using BookLibrary.CloudNative.Common.Behaviors;
using BookLibrary.CloudNative.Infrastructure.Messaging;
using BookLibrary.CloudNative.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Serilog;

// =============================================================================
// PROGRAM.CS — The complete Cloud-Native wiring
// =============================================================================
// This is the most involved Program.cs in our series. It registers:
//   1. PostgreSQL via EF Core
//   2. MediatR with THREE pipeline behaviours (Logging, Validation, Transaction)
//   3. FluentValidation
//   4. AWS SNS client (pointing to LocalStack in development)
//   5. SnsEventPublisher (our abstraction over the AWS SDK)
//   6. OutboxPublisherJob (the background service that reads and publishes events)
//   7. Serilog for structured JSON logging
// =============================================================================

// ── Structured logging with Serilog ──────────────────────────────────────────
// 📖 CONCEPT: Structured logging vs unstructured logging
//
// Unstructured (Console.WriteLine):
//   "Book 42 was published at 2025-01-15 by author John Doe"
//   → You can't query this. You can grep for "published" but can't filter by author.
//
// Structured (Serilog with properties):
//   { "BookId": 42, "Event": "Published", "AuthorName": "John Doe", "Timestamp": "2025-01-15" }
//   → You can query: "show all books published by John Doe in January"
//   → CloudWatch Insights, Datadog, Grafana Loki all understand this format.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "BookLibrary.CloudNative")
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

// ── Database ──────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

// ── MediatR with Pipeline Behaviours ──────────────────────────────────────────
// Pipeline order: Logging → Validation → Transaction → Handler
// Each behaviour wraps the next. The handler is deepest.
builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssemblyContaining<Program>();
    cfg.AddOpenBehavior(typeof(LoggingBehavior<,>));
    cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
    cfg.AddOpenBehavior(typeof(TransactionBehavior<,>)); // ← NEW in Project 4
});

// ── FluentValidation ──────────────────────────────────────────────────────────
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

// ── AWS SNS (pointing to LocalStack for local development) ────────────────────
// 📖 CONCEPT: In production, IAmazonSimpleNotificationService is created by
// the AWS SDK using the IAM role of the ECS/EC2 instance — no credentials needed.
// In local development, we point to LocalStack with dummy credentials.
var awsOptions = builder.Configuration.GetAWSOptions();
var localstackUrl = builder.Configuration["Aws:LocalStack:ServiceUrl"];

if (!string.IsNullOrEmpty(localstackUrl))
{
    // Local development mode: point SDK at LocalStack
    builder.Services.AddSingleton<IAmazonSimpleNotificationService>(_ =>
    {
        var config = new AmazonSimpleNotificationServiceConfig
        {
            ServiceURL = localstackUrl,
            AuthenticationRegion = "us-east-1"
        };
        var credentials = new BasicAWSCredentials("test", "test"); // LocalStack ignores these
        return new AmazonSimpleNotificationServiceClient(credentials, config);
    });
}
else
{
    // Production mode: use real AWS with IAM role credentials
    builder.Services.AddAWSService<IAmazonSimpleNotificationService>();
}

builder.Services.AddScoped<SnsEventPublisher>();

// ── Outbox Publisher Background Job ───────────────────────────────────────────
// AddHostedService registers OutboxPublisherJob as a Singleton IHostedService.
// It starts when the application starts and stops gracefully on shutdown.
builder.Services.AddHostedService<OutboxPublisherJob>();

// ── ASP.NET Core ──────────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Book Library API — Level 4: Cloud Native", Version = "v1" });
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

app.UseSerilogRequestLogging();
app.UseRouting();
app.MapControllers();

app.Run();

// ── ValidationBehavior (simplified, no reflection) ───────────────────────────
namespace BookLibrary.CloudNative.Common.Behaviors
{
    using FluentValidation;
    using MediatR;

    public class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        private readonly IEnumerable<IValidator<TRequest>> _validators;
        public ValidationBehavior(IEnumerable<IValidator<TRequest>> validators) => _validators = validators;

        public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
        {
            if (!_validators.Any()) return await next();
            var context = new ValidationContext<TRequest>(request);
            var failures = _validators.SelectMany(v => v.Validate(context).Errors).Where(f => f != null).ToList();
            if (!failures.Any()) return await next();
            throw new ValidationException(failures);
        }
    }
}
