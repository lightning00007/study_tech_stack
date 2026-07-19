using System.Text.Json;
using BookLibrary.CloudNative.Domain;
using BookLibrary.CloudNative.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BookLibrary.CloudNative.Infrastructure.Messaging;

// =============================================================================
// INFRASTRUCTURE/MESSAGING/OUTBOXPUBLISHERJOB.CS — The Outbox Background Worker
// =============================================================================
// This is the second half of the Outbox Pattern.
//
// RECALL THE PATTERN:
// 1. (SaveChangesAsync) Business data + OutboxMessage commit atomically to PostgreSQL
// 2. (THIS CLASS)       A background job reads unprocessed OutboxMessages
//                       and publishes them to SNS
//                       Then marks them as processed in the database
//
// WHY A BACKGROUND JOB?
// The HTTP request handler should return quickly. Publishing to SNS
// introduces network latency and potential timeouts. By moving it to a
// background job, we:
//   - Keep HTTP requests fast and predictable
//   - Allow retries without failing the HTTP request
//   - Decouple the HTTP layer from the messaging layer
//
// IHostedService is ASP.NET Core's way of running background tasks
// that start with the application and stop when the application shuts down.
// BackgroundService (which implements IHostedService) provides a simple
// ExecuteAsync() method that runs in the background.
//
// DELIVERY GUARANTEES:
// This job provides AT-LEAST-ONCE delivery:
//   - A message WILL be published eventually (guaranteed)
//   - It might be published more than once (if the job crashes after publishing
//     but before marking as processed)
// Consumers must handle duplicate events idempotently (by checking EventId).
// =============================================================================

/// <summary>
/// Background service that reads unprocessed outbox messages and publishes them to SNS.
/// Runs continuously while the application is alive, polling every 5 seconds.
/// </summary>
public class OutboxPublisherJob : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<OutboxPublisherJob> _logger;

    // ⚠️ GOTCHA: We inject IServiceProvider (not DbContext or SnsEventPublisher) directly.
    // BackgroundService is a Singleton — it lives for the entire application lifetime.
    // But DbContext and SnsEventPublisher are Scoped — they live per HTTP request.
    // You CANNOT inject Scoped services into Singletons directly — it causes a lifetime
    // mismatch. The solution: inject IServiceProvider and create a scope manually
    // inside ExecuteAsync.
    public OutboxPublisherJob(IServiceProvider serviceProvider, ILogger<OutboxPublisherJob> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OutboxPublisherJob started. Polling every 5 seconds.");

        // The job runs in a loop until the application shuts down (stoppingToken is cancelled)
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOutboxMessagesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in OutboxPublisherJob. Will retry in 5 seconds.");
            }

            // Wait 5 seconds before checking for new messages.
            // In production, you might use a longer interval or event-driven triggering.
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }

        _logger.LogInformation("OutboxPublisherJob stopped.");
    }

    private async Task ProcessOutboxMessagesAsync(CancellationToken ct)
    {
        // Create a new DI scope for this iteration.
        // This gives us fresh DbContext and SnsEventPublisher instances.
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<SnsEventPublisher>();

        // Query for unprocessed messages, ordered by creation time (FIFO delivery)
        // The partial index on (created_at WHERE processed_at IS NULL) makes this fast
        var unprocessed = await db.OutboxMessages
            .Where(m => m.ProcessedAt == null && m.RetryCount < 5) // Skip messages that keep failing
            .OrderBy(m => m.CreatedAt)
            .Take(20) // Process in batches of 20 to avoid long-running transactions
            .ToListAsync(ct);

        if (!unprocessed.Any()) return;

        _logger.LogDebug("Processing {Count} outbox messages...", unprocessed.Count);

        foreach (var message in unprocessed)
        {
            try
            {
                // Deserialise the event payload back to the correct concrete type.
                // We use the EventType field to determine which class to deserialise into.
                var domainEvent = DeserializeEvent(message.EventType, message.Payload);

                if (domainEvent is not null)
                {
                    await publisher.PublishAsync(domainEvent, ct);
                }

                // Mark as processed — this message won't be picked up again
                message.ProcessedAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to publish outbox message {MessageId} (EventType: {EventType}). RetryCount: {RetryCount}",
                    message.Id, message.EventType, message.RetryCount);

                message.Error = ex.Message;
                message.RetryCount++;
                // ProcessedAt stays null — the message will be retried
            }
        }

        // Save all status updates in one batch (efficient)
        await db.SaveChangesAsync(ct);
    }

    private static IDomainEvent? DeserializeEvent(string eventType, string payload)
    {
        // Map event type names to concrete types for deserialisation
        // In a production system, you'd use a type registry or reflection
        return eventType switch
        {
            "BookCreatedEvent" => JsonSerializer.Deserialize<BookCreatedEvent>(payload),
            "BookPublishedEvent" => JsonSerializer.Deserialize<BookPublishedEvent>(payload),
            _ => null
        };
    }
}
