using System.Text.Json;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using BookLibrary.CloudNative.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BookLibrary.CloudNative.Infrastructure.Messaging;

// =============================================================================
// INFRASTRUCTURE/MESSAGING/SNSEVENTPUBLISHER.CS — AWS SNS Publisher
// =============================================================================
// This class is responsible for publishing domain events to AWS SNS topics.
//
// HOW SNS WORKS:
// SNS (Simple Notification Service) is a pub/sub message broker.
//   - Publisher: BookLibrary publishes "BookCreatedEvent" to an SNS topic
//   - Topic: "book-library-events" — a named channel
//   - Subscribers: Any service that has subscribed to this topic receives the message
//     (Email notification service, Search indexer SQS queue, Analytics pipeline)
//
// WHY SNS + SQS together?
// SNS delivers messages immediately to all subscribers. If a subscriber is
// temporarily unavailable, it MISSES the message.
//
// SQS (Simple Queue Service) is a durable message queue. Subscribers subscribe
// to SNS via an SQS queue. If the subscriber is down, SQS holds the message
// until the subscriber comes back up.
//
//   BookLibrary → SNS Topic → SQS Queue → Email Service consumer
//                           → SQS Queue → Search Indexer consumer
//
// LOCAL DEVELOPMENT WITH LOCALSTACK:
// LocalStack is a Docker container that emulates AWS services locally.
// By pointing the AWS SDK at http://localhost:4566 instead of real AWS,
// we can develop and test SNS/SQS integration without an AWS account.
// See docker-compose.yml for the LocalStack setup.
// =============================================================================

/// <summary>
/// Publishes domain events to AWS SNS.
/// In local development, points to LocalStack instead of real AWS.
/// </summary>
public class SnsEventPublisher
{
    private readonly IAmazonSimpleNotificationService _sns;
    private readonly ILogger<SnsEventPublisher> _logger;
    private readonly string _topicArn;

    public SnsEventPublisher(
        IAmazonSimpleNotificationService sns,
        IConfiguration configuration,
        ILogger<SnsEventPublisher> logger)
    {
        _sns = sns;
        _logger = logger;
        // The topic ARN is configured in appsettings.json.
        // In production: loaded from AWS Parameter Store.
        // In local dev:  LocalStack generates it (see appsettings.json).
        _topicArn = configuration["Aws:Sns:BookEventsTopicArn"]
            ?? throw new InvalidOperationException("Aws:Sns:BookEventsTopicArn configuration is required.");
    }

    /// <summary>
    /// Publishes a domain event to the book-events SNS topic.
    /// Called by the OutboxPublisherJob background service.
    /// </summary>
    public async Task PublishAsync(IDomainEvent domainEvent, CancellationToken ct = default)
    {
        var eventTypeName = domainEvent.GetType().Name;

        // 📖 CONCEPT: Message envelope pattern
        // We wrap the payload in an envelope with metadata:
        //   - EventId: for idempotent consumers (don't process the same event twice)
        //   - EventType: for routing (consumers know which handler to call)
        //   - OccurredAt: for ordering (process events in the right sequence)
        //   - Payload: the actual event data
        var envelope = new
        {
            EventId = domainEvent.EventId,
            EventType = eventTypeName,
            OccurredAt = domainEvent.OccurredAt,
            Payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType())
        };

        var message = JsonSerializer.Serialize(envelope);

        var request = new PublishRequest
        {
            TopicArn = _topicArn,
            Message = message,

            // 📖 CONCEPT: SNS Message Attributes for filtering
            // SQS subscribers can filter which messages they receive.
            // A search indexer might only subscribe to BookCreatedEvent and BookPublishedEvent.
            // An analytics service might subscribe to ALL events.
            // This filtering happens at the SNS layer — irrelevant messages never reach the queue.
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["EventType"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = eventTypeName
                }
            }
        };

        var response = await _sns.PublishAsync(request, ct);

        _logger.LogInformation(
            "Published {EventType} (EventId: {EventId}) to SNS. MessageId: {MessageId}",
            eventTypeName, domainEvent.EventId, response.MessageId);
    }
}
