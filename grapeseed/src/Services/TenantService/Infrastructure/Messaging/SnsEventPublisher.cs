using System.Text.Json;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using GrapeSeed.SharedKernel.Application.Messaging;
using GrapeSeed.SharedKernel.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace GrapeSeed.TenantService.Infrastructure.Messaging;

// =============================================================================
// 📖 CONCEPT: SNS Event Publisher
// =============================================================================
// This class implements IEventPublisher using AWS SNS.
// It is the Infrastructure layer's concrete implementation of the domain's
// abstract event publishing contract.
//
// How messages are routed:
//   Each domain event type maps to a specific SNS Topic ARN.
//   When TenantRegisteredEvent is published, it goes to the
//   "grapeseed-tenant-registered" SNS topic. All SQS queues subscribed
//   to that topic receive a copy of the message.
//
// Message format:
//   We publish a JSON envelope with metadata (EventType, EventId, Timestamp)
//   plus the serialised event payload. This envelope allows consumers to:
//     - Route messages to the correct handler based on EventType.
//     - Deduplicate using EventId.
//     - Debug by examining the Timestamp.
//
// 🔗 SEE ALSO: docs/04-aws-services.md#41-the-event-bus-sns--sqs
// =============================================================================

/// <summary>
/// Publishes domain events to AWS SNS topics.
/// Used by the Outbox background job to deliver events durably.
/// </summary>
public sealed class SnsEventPublisher : IEventPublisher
{
    private readonly IAmazonSimpleNotificationService _sns;
    private readonly ILogger<SnsEventPublisher> _logger;

    // Topic ARNs are loaded from configuration (AWS Parameter Store in production)
    private readonly Dictionary<string, string> _topicArns;

    public SnsEventPublisher(
        IAmazonSimpleNotificationService sns,
        IConfiguration configuration,
        ILogger<SnsEventPublisher> logger)
    {
        _sns = sns;
        _logger = logger;

        // 📖 CONCEPT: Topic ARN mapping
        // Each event type maps to a named SNS topic.
        // In production, these ARNs come from AWS Parameter Store or environment variables.
        _topicArns = new Dictionary<string, string>
        {
            ["TenantRegisteredEvent"]  = configuration["Aws:Sns:TenantRegisteredTopicArn"]!,
            ["TenantActivatedEvent"]   = configuration["Aws:Sns:TenantActivatedTopicArn"]!,
            ["TenantSuspendedEvent"]   = configuration["Aws:Sns:TenantSuspendedTopicArn"]!,
        };
    }

    public async Task PublishAsync(IDomainEvent domainEvent, CancellationToken ct = default)
    {
        var eventTypeName = domainEvent.GetType().Name;

        if (!_topicArns.TryGetValue(eventTypeName, out var topicArn))
        {
            // 📖 CONCEPT: Not all domain events are published externally.
            // Some are consumed only within the same service (e.g., for side-effects like
            // sending an internal email notification).
            _logger.LogDebug("No SNS topic configured for event type {EventType}. Skipping external publish.", eventTypeName);
            return;
        }

        // Build the message envelope
        var envelope = new SnsMessageEnvelope
        {
            EventId = domainEvent.EventId.ToString(),
            EventType = eventTypeName,
            OccurredAt = domainEvent.OccurredAt,
            // 📖 CONCEPT: Polymorphic serialisation — we serialise as the concrete type
            // so the payload contains all the event-specific fields.
            Payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType())
        };

        var messageBody = JsonSerializer.Serialize(envelope);

        var request = new PublishRequest
        {
            TopicArn = topicArn,
            Message = messageBody,

            // 📖 CONCEPT: SNS Message Attributes
            // Attributes allow SQS subscribers to filter messages.
            // For example, IdentityService's SQS subscription can filter to receive
            // ONLY TenantRegisteredEvent and TenantSuspendedEvent, ignoring billing events.
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["EventType"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = eventTypeName
                }
            }
        };

        try
        {
            var response = await _sns.PublishAsync(request, ct);
            _logger.LogInformation(
                "Published {EventType} (EventId: {EventId}) to SNS. MessageId: {MessageId}",
                eventTypeName, domainEvent.EventId, response.MessageId);
        }
        catch (Exception ex)
        {
            // ⚠️ GOTCHA: If SNS publish fails, do NOT throw immediately.
            // The Outbox publisher will retry this message.
            // Just log the error and let the OutboxPublisher handle retry logic.
            _logger.LogError(ex, "Failed to publish {EventType} to SNS. Will retry via Outbox.", eventTypeName);
            throw; // Re-throw so OutboxPublisher increments RetryCount
        }
    }

    private sealed class SnsMessageEnvelope
    {
        public string EventId { get; init; } = string.Empty;
        public string EventType { get; init; } = string.Empty;
        public DateTime OccurredAt { get; init; }
        public string Payload { get; init; } = string.Empty;
    }
}
