# Chapter 4: AWS SQS & SNS — Decoupled Messaging

---

## 4.1 The Problem: Tight Coupling

Imagine you have an e-commerce application. When a customer places an order, you need to:
1. Send a confirmation email
2. Update inventory
3. Notify the warehouse
4. Update analytics dashboards
5. Charge the payment method

The **naive approach** — do all of this synchronously inside the `PlaceOrder` API endpoint — has serious problems:
- If any step fails, the whole order fails (atomicity risk)
- If email service is slow, the user waits 10 seconds for an order confirmation
- All services are tightly coupled — you can't deploy them independently
- A spike in orders overwhelms every downstream system simultaneously

**The solution: Message queues and topics** — the Order service drops a message on a queue, returns success immediately to the user, and downstream services process the message independently.

---

## 4.2 AWS SQS — Simple Queue Service

SQS is a fully managed **message queue** service. It provides a reliable buffer between producers and consumers.

### 4.2.1 Core Concepts

| Concept | Description |
|---|---|
| **Queue** | The storage for messages. Lives in a specific AWS region. |
| **Message** | The unit of work. Up to 256KB of text (JSON, XML, plain text). For larger payloads, store in S3 and send the S3 key. |
| **Producer** | The application that sends messages to the queue. |
| **Consumer** | The application that reads and processes messages. |
| **Visibility Timeout** | When a consumer reads a message, it becomes "invisible" to other consumers for N seconds. If not deleted before timeout, it becomes visible again (retry). |
| **Message Retention** | How long SQS keeps unprocessed messages. Default: 4 days. Max: 14 days. |
| **Dead Letter Queue (DLQ)** | A separate queue for messages that fail processing N times. |

### 4.2.2 Standard vs FIFO Queues

| Feature | Standard Queue | FIFO Queue |
|---|---|---|
| **Throughput** | Nearly unlimited | Up to 300 msg/sec (or 3000 with batching) |
| **Ordering** | Best-effort (mostly in order) | Strict first-in, first-out |
| **Delivery** | At-least-once (occasional duplicates) | Exactly-once (deduplication) |
| **Use case** | High-throughput jobs where order doesn't matter (email, analytics) | Payment processing, order state machines (order matters) |
| **Name suffix** | `my-queue` | `my-queue.fifo` (must end with `.fifo`) |

### 4.2.3 How Visibility Timeout Works

This is the most important concept for building reliable SQS consumers:

```
Timeline:

T+0s:  Consumer A calls ReceiveMessage. Gets message M.
       Message M is now INVISIBLE to all other consumers.
       Visibility Timeout: 30 seconds.

T+15s: Consumer A is still processing. It calls ChangeMessageVisibility
       to extend the timeout (good practice for long processing).

T+20s: Consumer A finishes successfully. Calls DeleteMessage.
       Message M is permanently removed from queue. ✅

--- FAILURE SCENARIO ---

T+0s:  Consumer A gets message M. Visibility Timeout: 30 seconds.
T+5s:  Consumer A CRASHES.
T+30s: Visibility Timeout expires. Message M becomes VISIBLE again.
T+31s: Consumer B gets message M. Processes it successfully. Deletes it. ✅

--- DLQ SCENARIO ---

After N failures (e.g., 3 retries), message M goes to Dead Letter Queue.
An engineer inspects the DLQ, fixes the bug, and re-queues the message.
```

### 4.2.4 Long Polling vs Short Polling

```
Short Polling (WaitTimeSeconds = 0):
- SQS checks a SUBSET of servers and returns immediately
- May return empty even if messages exist (false empty)
- You poll frequently → more API calls → higher cost

Long Polling (WaitTimeSeconds = 1-20):
- SQS checks ALL servers and waits up to N seconds for a message
- More accurate, reduces empty responses
- Fewer API calls → lower cost
- ALWAYS use long polling in production (WaitTimeSeconds = 20)
```

### 4.2.5 SQS in .NET — Complete Consumer

```csharp
// Install: dotnet add package AWSSDK.SQS

// Program.cs setup
builder.Services.AddDefaultAWSOptions(builder.Configuration.GetAWSOptions());
builder.Services.AddAWSService<IAmazonSQS>();
builder.Services.AddHostedService<OrderQueueConsumer>();

// ── Producer ───────────────────────────────────────────────────────────────

public class OrderQueueProducer
{
    private readonly IAmazonSQS _sqs;
    private readonly ILogger<OrderQueueProducer> _logger;
    private const string QueueUrl = "https://sqs.ap-southeast-1.amazonaws.com/123456789012/order-events.fifo";

    public OrderQueueProducer(IAmazonSQS sqs, ILogger<OrderQueueProducer> logger)
    {
        _sqs = sqs;
        _logger = logger;
    }

    public async Task SendOrderCreatedAsync(OrderCreatedEvent evt, CancellationToken ct = default)
    {
        var messageBody = JsonSerializer.Serialize(evt);

        var request = new SendMessageRequest
        {
            QueueUrl = QueueUrl,
            MessageBody = messageBody,

            // For FIFO queues: MessageGroupId groups related messages for ordering
            MessageGroupId = $"user-{evt.UserId}",

            // For FIFO queues: MessageDeduplicationId prevents duplicate processing
            // within a 5-minute window
            MessageDeduplicationId = $"order-{evt.OrderId}-{evt.Timestamp:yyyyMMddHHmmss}",

            // Message attributes (metadata searchable without parsing body)
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["EventType"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = "OrderCreated"
                },
                ["Version"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = "1.0"
                }
            },

            // Delay delivery (standard queues only) — process after 5 minutes
            DelaySeconds = 0
        };

        try
        {
            var response = await _sqs.SendMessageAsync(request, ct);
            _logger.LogInformation("Message sent. MessageId: {MessageId}", response.MessageId);
        }
        catch (AmazonSQSException ex)
        {
            _logger.LogError(ex, "Failed to send message for order {OrderId}", evt.OrderId);
            throw;
        }
    }

    // Send multiple messages in one API call (more efficient, max 10 per batch)
    public async Task SendBatchAsync(IEnumerable<OrderCreatedEvent> events, CancellationToken ct = default)
    {
        var entries = events.Select((evt, i) => new SendMessageBatchRequestEntry
        {
            Id = i.ToString(),           // Unique ID within batch for error tracking
            MessageBody = JsonSerializer.Serialize(evt),
            MessageGroupId = $"user-{evt.UserId}"
        }).ToList();

        var response = await _sqs.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = QueueUrl,
            Entries = entries
        }, ct);

        if (response.Failed.Count > 0)
        {
            foreach (var failure in response.Failed)
                _logger.LogError("Batch send failed for entry {Id}: {Message}", failure.Id, failure.Message);
        }
    }
}

// ── Consumer (BackgroundService) ──────────────────────────────────────────

public class OrderQueueConsumer : BackgroundService
{
    private readonly IAmazonSQS _sqs;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OrderQueueConsumer> _logger;
    private const string QueueUrl = "https://sqs.ap-southeast-1.amazonaws.com/123456789012/order-events.fifo";
    private const int VisibilityTimeoutSeconds = 60;

    public OrderQueueConsumer(IAmazonSQS sqs, IServiceScopeFactory scopeFactory, ILogger<OrderQueueConsumer> logger)
    {
        _sqs = sqs;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SQS Consumer started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAndProcessAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error in SQS polling loop. Retrying in 5 seconds...");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task PollAndProcessAsync(CancellationToken ct)
    {
        var response = await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = QueueUrl,
            MaxNumberOfMessages = 10,         // Max 10 per API call (SQS limit)
            WaitTimeSeconds = 20,             // Long polling
            VisibilityTimeout = VisibilityTimeoutSeconds,
            MessageAttributeNames = new List<string> { "All" },
            AttributeNames = new List<string> { "All" }
        }, ct);

        if (!response.Messages.Any()) return;

        // Process all messages concurrently (be careful not to overwhelm downstream)
        var tasks = response.Messages.Select(msg => ProcessSingleMessageAsync(msg, ct));
        await Task.WhenAll(tasks);
    }

    private async Task ProcessSingleMessageAsync(Message message, CancellationToken ct)
    {
        _logger.LogInformation("Processing message {MessageId}", message.MessageId);

        // Create a scope for scoped services (DbContext, etc.)
        using var scope = _scopeFactory.CreateScope();

        try
        {
            var eventType = message.MessageAttributes.TryGetValue("EventType", out var attr)
                ? attr.StringValue : "Unknown";

            switch (eventType)
            {
                case "OrderCreated":
                    var evt = JsonSerializer.Deserialize<OrderCreatedEvent>(message.Body)
                        ?? throw new InvalidOperationException("Could not deserialize message");

                    var handler = scope.ServiceProvider.GetRequiredService<IOrderCreatedHandler>();
                    await handler.HandleAsync(evt, ct);
                    break;

                default:
                    _logger.LogWarning("Unknown event type: {EventType}. Skipping.", eventType);
                    break;
            }

            // SUCCESS: Delete the message from the queue
            await _sqs.DeleteMessageAsync(QueueUrl, message.ReceiptHandle, ct);
            _logger.LogInformation("Message {MessageId} processed and deleted", message.MessageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process message {MessageId}. Will be retried.", message.MessageId);
            // DO NOT delete the message — SQS will retry after VisibilityTimeout
            // After MaxReceiveCount retries, it moves to DLQ automatically
        }
    }
}
```

---

## 4.3 AWS SNS — Simple Notification Service

SNS is a fully managed **pub/sub (publish-subscribe)** messaging service. One publisher can send a message to many subscribers simultaneously.

### 4.3.1 Core Concepts

| Concept | Description |
|---|---|
| **Topic** | The channel where publishers send messages. All subscribers receive copies. |
| **Publisher** | Application that sends messages to the topic. |
| **Subscriber** | Can be: SQS queue, Lambda function, HTTP/HTTPS endpoint, Email, SMS, Mobile Push |
| **Fan-out** | One message → multiple subscribers. Each gets an independent copy. |
| **Message Filtering** | Subscribers declare filter policies — they only receive messages matching their filter |

### 4.3.2 SNS Fan-out Architecture

```
[Order Service]
     │
     │ Publish("OrderPlaced", { orderId: 42, userId: 7, total: 150.00 })
     ▼
[SNS Topic: order-events]
     │
     ├── Filter: {"eventType": ["OrderPlaced"]}
     │   └──► [SQS: email-queue]
     │              └──► [Email Service Lambda] → sends confirmation email
     │
     ├── Filter: {"eventType": ["OrderPlaced", "OrderCancelled"]}
     │   └──► [SQS: inventory-queue]
     │              └──► [Inventory Service] → updates stock
     │
     ├── Filter: {"eventType": ["OrderPlaced"], "totalAmount": [{"numeric": [">=", 500]}]}
     │   └──► [SQS: high-value-queue]
     │              └──► [Fraud Detection Service]
     │
     └──► [HTTP endpoint: analytics.internal/events]
               → Analytics dashboard real-time update
```

### 4.3.3 Message Filtering

Without filtering, every subscriber receives EVERY message. Filtering lets each subscriber declare which messages it cares about.

```json
// Email service filter policy (only OrderPlaced and OrderShipped events)
{
  "eventType": ["OrderPlaced", "OrderShipped"],
  "channel": ["email", "all"]
}

// Fraud detection filter policy (only high-value orders)
{
  "eventType": ["OrderPlaced"],
  "totalAmount": [{"numeric": [">=", 500]}]
}

// VIP customer service filter policy
{
  "eventType": ["OrderPlaced"],
  "isVip": [true]
}
```

### 4.3.4 SNS in .NET

```csharp
// Install: dotnet add package AWSSDK.SimpleNotificationService

public class EventBus
{
    private readonly IAmazonSimpleNotificationService _sns;
    private const string TopicArn = "arn:aws:sns:ap-southeast-1:123456789012:order-events";

    public EventBus(IAmazonSimpleNotificationService sns) => _sns = sns;

    public async Task PublishAsync<T>(string eventType, T payload, CancellationToken ct = default)
        where T : class
    {
        var message = JsonSerializer.Serialize(payload);

        var request = new PublishRequest
        {
            TopicArn = TopicArn,
            Message = message,
            Subject = eventType,  // shown in email notifications

            // Message attributes — used by SNS filter policies
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["eventType"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = eventType
                }
            }
        };

        // Add numeric attributes for numeric filtering
        if (payload is OrderPlacedEvent orderEvt)
        {
            request.MessageAttributes["totalAmount"] = new MessageAttributeValue
            {
                DataType = "Number",
                StringValue = orderEvt.Total.ToString("F2")
            };
            request.MessageAttributes["isVip"] = new MessageAttributeValue
            {
                DataType = "String",
                StringValue = orderEvt.IsVip.ToString().ToLower()
            };
        }

        var response = await _sns.PublishAsync(request, ct);
        // MessageId is unique per SNS delivery
    }

    // Publish multiple messages (max 10 per batch)
    public async Task PublishBatchAsync(IEnumerable<(string EventType, string Message)> messages, CancellationToken ct = default)
    {
        var entries = messages.Select((m, i) => new PublishBatchRequestEntry
        {
            Id = i.ToString(),
            Message = m.Message,
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["eventType"] = new MessageAttributeValue { DataType = "String", StringValue = m.EventType }
            }
        }).ToList();

        await _sns.PublishBatchAsync(new PublishBatchRequest { TopicArn = TopicArn, PublishBatchRequestEntries = entries }, ct);
    }
}
```

### 4.3.5 SNS → SQS: The Large Payload Pattern

SQS and SNS have a 256KB message size limit. For larger payloads (e.g., images, large JSON documents), use the **S3 Extended Client pattern** or the **Claim Check pattern**:

```csharp
// Claim Check Pattern: Store large payload in S3, send only the reference
public async Task PublishLargeEventAsync(string eventType, byte[] largePayload, CancellationToken ct = default)
{
    // 1. Store large payload in S3
    var s3Key = $"events/{Guid.NewGuid()}.json";
    await _s3.PutObjectAsync(new PutObjectRequest
    {
        BucketName = "event-payloads",
        Key = s3Key,
        InputStream = new MemoryStream(largePayload),
        ContentType = "application/json"
    }, ct);

    // 2. Publish only the reference to SNS
    await PublishAsync(eventType, new EventReference
    {
        S3Bucket = "event-payloads",
        S3Key = s3Key,
        PayloadSizeBytes = largePayload.Length
    }, ct);
}

// Consumer: fetch actual payload from S3
public async Task<T> ResolveLargePayloadAsync<T>(EventReference reference, CancellationToken ct = default)
{
    var s3Response = await _s3.GetObjectAsync(reference.S3Bucket, reference.S3Key, ct);
    using var reader = new StreamReader(s3Response.ResponseStream);
    var json = await reader.ReadToEndAsync(ct);
    return JsonSerializer.Deserialize<T>(json)!;
}
```

---

## 4.4 Dead Letter Queue (DLQ) — Handling Failures

A DLQ is a second queue where messages go after failing processing N times (configured as `maxReceiveCount`). This is essential for production systems.

```
Main Queue (maxReceiveCount = 3)
    ↓ message fails 3 times
Dead Letter Queue
    ↓ monitored and alarmed
Engineer inspects, fixes bug, and re-queues
```

### DLQ Strategy in .NET

```csharp
// DLQ Consumer — for inspection and replay
public class DlqInspectionService
{
    private readonly IAmazonSQS _sqs;
    private const string DlqUrl = "https://sqs.ap-southeast-1.amazonaws.com/123456789012/order-events-dlq";

    // Get DLQ messages for inspection (without deleting them)
    public async Task<List<Message>> InspectDlqAsync(int count = 10)
    {
        var response = await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = DlqUrl,
            MaxNumberOfMessages = count,
            VisibilityTimeout = 300,  // give 5 minutes to inspect
            WaitTimeSeconds = 5,
            AttributeNames = new List<string> { "All" }
        });

        return response.Messages;
    }

    // Replay a message from DLQ back to main queue
    public async Task ReplayMessageAsync(string receiptHandle, string messageBody, CancellationToken ct = default)
    {
        const string MainQueueUrl = "https://sqs.../order-events.fifo";

        // Send to main queue
        await _sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = MainQueueUrl,
            MessageBody = messageBody,
            MessageGroupId = "replay",
            MessageDeduplicationId = Guid.NewGuid().ToString()
        }, ct);

        // Delete from DLQ
        await _sqs.DeleteMessageAsync(DlqUrl, receiptHandle, ct);
    }
}
```

---

## Summary — Chapter 4

| Concept | Key Takeaway |
|---|---|
| **SQS Standard** | High-throughput, at-least-once, use for fire-and-forget jobs |
| **SQS FIFO** | Ordered, exactly-once, use for stateful workflows (payments) |
| **Visibility Timeout** | The retry mechanism — don't delete on failure, it'll be retried |
| **Long Polling** | Always use WaitTimeSeconds=20 in production |
| **SNS Fan-out** | One event → many services, each independently subscribing |
| **Message Filtering** | Subscribers only get relevant messages — keeps systems clean |
| **DLQ** | Never lose a failed message — always configure a DLQ |
| **Claim Check** | For payloads > 256KB: store in S3, send S3 key in queue |

---

# Chapter 5: S3 & CloudFront — Object Storage and CDN

---

## 5.1 AWS S3 — Simple Storage Service

S3 is AWS's **object storage service**. An "object" is a file plus its metadata. S3 stores objects in **buckets**. Key characteristics:
- **Virtually unlimited storage** — no pre-provisioned capacity
- **11 nines of durability** (99.999999999%) — AWS stores objects across multiple AZs
- **Pay per use** — per GB stored, per API request, per GB egress
- Not a filesystem (no folders, no append, no partial reads) — objects are replaced wholesale

### 5.1.1 Core Concepts

| Term | Meaning |
|---|---|
| **Bucket** | Container for objects. Globally unique name across all AWS. |
| **Object** | A file + metadata. Identified by its **Key** (the file path). |
| **Key** | The object's "path" within a bucket. E.g., `uploads/2024/06/photo.jpg` |
| **Prefix** | A key prefix acts like a folder: `uploads/2024/` |
| **Region** | Bucket belongs to one region. Objects are stored across multiple AZs in that region. |
| **Version ID** | If versioning is enabled, each object modification creates a new version. |
| **ETag** | MD5 hash of the object content — useful for change detection. |

### 5.1.2 Storage Classes — Optimize Cost

S3 has multiple storage tiers. You pay less for less frequently accessed data:

| Storage Class | Use Case | Retrieval | Cost |
|---|---|---|---|
| **S3 Standard** | Frequently accessed data | Milliseconds | Highest storage cost |
| **S3 Intelligent-Tiering** | Unknown or changing access patterns | Milliseconds | Automatic cost optimization |
| **S3 Standard-IA** | Infrequently Accessed, but fast when needed | Milliseconds | ~40% cheaper than Standard |
| **S3 One Zone-IA** | IA but okay with single-AZ (less durable) | Milliseconds | ~20% cheaper than IA |
| **S3 Glacier Instant Retrieval** | Archives accessed quarterly | Milliseconds | Very cheap |
| **S3 Glacier Flexible Retrieval** | Disaster recovery, archives | Minutes to hours | Extremely cheap |
| **S3 Glacier Deep Archive** | Long-term archival (7+ years) | Hours | Cheapest |

### 5.1.3 Lifecycle Policies — Automate Storage Tiering

```json
// S3 Lifecycle Policy — automatically move or delete objects over time
{
  "Rules": [
    {
      "Id": "LogArchiving",
      "Status": "Enabled",
      "Filter": { "Prefix": "logs/" },
      "Transitions": [
        {
          "Days": 30,
          "StorageClass": "STANDARD_IA"
        },
        {
          "Days": 90,
          "StorageClass": "GLACIER"
        }
      ],
      "Expiration": {
        "Days": 365
      }
    },
    {
      "Id": "DeleteTempUploads",
      "Status": "Enabled",
      "Filter": { "Prefix": "temp/" },
      "Expiration": { "Days": 1 }
    }
  ]
}
```

### 5.1.4 S3 in .NET — Complete File Service

```csharp
// Install: dotnet add package AWSSDK.S3

public class S3FileService
{
    private readonly IAmazonS3 _s3;
    private readonly ILogger<S3FileService> _logger;
    private const string BucketName = "my-app-uploads";
    private const string CloudFrontDomain = "https://d1234abcd.cloudfront.net";

    public S3FileService(IAmazonS3 s3, ILogger<S3FileService> logger)
    {
        _s3 = s3;
        _logger = logger;
    }

    // ── Upload ────────────────────────────────────────────────────────────────

    public async Task<string> UploadAsync(
        Stream stream,
        string originalFileName,
        string contentType,
        string folder = "uploads",
        CancellationToken ct = default)
    {
        // Generate a unique key to prevent collisions and expose no user data in URLs
        var extension = Path.GetExtension(originalFileName);
        var key = $"{folder}/{DateTime.UtcNow:yyyy/MM/dd}/{Guid.NewGuid()}{extension}";

        var request = new PutObjectRequest
        {
            BucketName = BucketName,
            Key = key,
            InputStream = stream,
            ContentType = contentType,
            ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256,
            // Store original filename as metadata
            Metadata = { ["original-filename"] = originalFileName }
        };

        // Set cache headers for CloudFront
        request.Headers.CacheControl = contentType.StartsWith("image/")
            ? "public, max-age=31536000, immutable"  // images: cache 1 year
            : "private, no-cache";                    // documents: don't cache

        await _s3.PutObjectAsync(request, ct);
        _logger.LogInformation("Uploaded {Key} ({ContentType})", key, contentType);

        return $"{CloudFrontDomain}/{key}";
    }

    // ── Multipart Upload (for large files > 100MB) ────────────────────────────

    public async Task<string> UploadLargeFileAsync(
        Stream stream,
        string key,
        string contentType,
        CancellationToken ct = default)
    {
        // Initiate multipart upload
        var initiateResponse = await _s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = BucketName,
            Key = key,
            ContentType = contentType,
            ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256
        }, ct);

        var uploadId = initiateResponse.UploadId;
        var partETags = new List<PartETag>();
        const int partSize = 5 * 1024 * 1024; // 5MB minimum part size
        var buffer = new byte[partSize];
        var partNumber = 1;

        try
        {
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer, 0, partSize, ct)) > 0)
            {
                using var partStream = new MemoryStream(buffer, 0, bytesRead);

                var uploadPartResponse = await _s3.UploadPartAsync(new UploadPartRequest
                {
                    BucketName = BucketName,
                    Key = key,
                    UploadId = uploadId,
                    PartNumber = partNumber++,
                    InputStream = partStream,
                    IsLastPart = bytesRead < partSize
                }, ct);

                partETags.Add(new PartETag(uploadPartResponse.PartNumber, uploadPartResponse.ETag));
            }

            // Complete the multipart upload
            await _s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = BucketName,
                Key = key,
                UploadId = uploadId,
                PartETags = partETags
            }, ct);

            return $"{CloudFrontDomain}/{key}";
        }
        catch
        {
            // Abort the upload to avoid orphan parts (you pay for them)
            await _s3.AbortMultipartUploadAsync(BucketName, key, uploadId, ct);
            throw;
        }
    }

    // ── Pre-signed URL (temporary access to private files) ───────────────────

    public string GeneratePresignedDownloadUrl(string key, int expiryMinutes = 60)
    {
        // Use this for: invoices, personal documents, private videos
        // The URL works even for anonymous users for the duration
        var request = new GetPreSignedUrlRequest
        {
            BucketName = BucketName,
            Key = key,
            Expires = DateTime.UtcNow.AddMinutes(expiryMinutes),
            Verb = HttpVerb.GET,
            Protocol = Protocol.HTTPS
        };
        return _s3.GetPreSignedURL(request);
    }

    // Pre-signed PUT URL — let client upload directly to S3 (bypasses your server)
    // Great for large file uploads: client gets a URL, uploads directly to S3
    public string GeneratePresignedUploadUrl(string key, string contentType, int expiryMinutes = 15)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = BucketName,
            Key = key,
            Verb = HttpVerb.PUT,
            Expires = DateTime.UtcNow.AddMinutes(expiryMinutes),
            ContentType = contentType,
            Protocol = Protocol.HTTPS
        };
        return _s3.GetPreSignedURL(request);
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        await _s3.DeleteObjectAsync(BucketName, key, ct);
    }

    // Batch delete (up to 1000 objects per call)
    public async Task DeleteManyAsync(IEnumerable<string> keys, CancellationToken ct = default)
    {
        var deleteRequest = new DeleteObjectsRequest
        {
            BucketName = BucketName,
            Objects = keys.Select(k => new KeyVersion { Key = k }).ToList()
        };
        await _s3.DeleteObjectsAsync(deleteRequest, ct);
    }

    // ── Copy (e.g., move from temp to permanent location) ────────────────────

    public async Task<string> CopyAsync(string sourceKey, string destinationFolder, CancellationToken ct = default)
    {
        var extension = Path.GetExtension(sourceKey);
        var destKey = $"{destinationFolder}/{Guid.NewGuid()}{extension}";

        await _s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = BucketName,
            SourceKey = sourceKey,
            DestinationBucket = BucketName,
            DestinationKey = destKey,
            ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256
        }, ct);

        return destKey;
    }
}
```

---

## 5.2 CloudFront — Content Delivery Network

CloudFront is AWS's **CDN (Content Delivery Network)**. It has 400+ **edge locations** (Points of Presence) around the world. When a user requests content, CloudFront serves it from the nearest edge location — dramatically reducing latency.

### 5.2.1 How CloudFront Works

```
Request Flow (Cache HIT):
User (Ho Chi Minh City)
  → CloudFront Edge (Singapore)  ← cached copy served here (fast!)
  
Request Flow (Cache MISS):
User (Ho Chi Minh City)
  → CloudFront Edge (Singapore)  ← not cached, forwards to origin
    → S3 Origin (us-east-1)      ← fetches object
    → CloudFront Edge (Singapore) ← caches the response
  → User                          ← user finally gets the response
  
Next request for same object: served from Singapore edge (cache HIT) ✅
```

### 5.2.2 Origin Access Control (OAC) — The Secure Pattern

**Never make your S3 bucket public.** Use OAC:
- S3 bucket is **private** (no public access)
- CloudFront has a special IAM identity (OAC) that S3 trusts
- Only CloudFront can read from S3
- Users access files ONLY through CloudFront URLs

This protects your files from unauthorized access and reduces S3 egress costs (CloudFront egress is cheaper than S3 egress).

### 5.2.3 Cache Behaviors

CloudFront lets you define different caching rules for different URL patterns:

```
URL Pattern         | Cache TTL  | Compress | Forward Headers
--------------------|------------|----------|------------------
/static/*           | 1 year     | Yes      | None (fully public)
/images/*           | 30 days    | Yes      | None
/api/*              | No cache   | No       | Authorization, Cookie
/videos/*.m3u8      | 5 minutes  | No       | None (playlists change)
/videos/*.ts        | 24 hours   | No       | None (segments are immutable)
```

### 5.2.4 Signed URLs and Signed Cookies

For **premium content** (paid videos, private documents), you don't want anyone with a URL to access the file. CloudFront Signed URLs add an expiring signature:

```csharp
// Install: dotnet add package Amazon.CloudFront

public class CloudFrontSignedUrlService
{
    private readonly string _keyPairId;      // CloudFront Key Pair ID (from AWS console)
    private readonly string _privateKeyPath; // RSA private key file path

    public CloudFrontSignedUrlService(IConfiguration config)
    {
        _keyPairId = config["CloudFront:KeyPairId"]!;
        _privateKeyPath = config["CloudFront:PrivateKeyPath"]!;
    }

    public string GenerateSignedUrl(string resourceUrl, DateTime expiresAt)
    {
        return AmazonCloudFrontUrlSigner.GetCannedSignedURL(
            resourceUrl,
            new StreamReader(_privateKeyPath),
            _keyPairId,
            expiresAt
        );
    }

    // Signed Cookie — better for HLS video streaming (avoids signing each .ts segment URL)
    public (string Policy, string Signature, string KeyPairId) GenerateSignedCookies(
        string resourcePattern,  // e.g., "https://d1234.cloudfront.net/videos/42/*"
        DateTime expiresAt)
    {
        var policy = AmazonCloudFrontUrlSigner.BuildPolicyForSignedUrl(
            resourcePattern, expiresAt, ipRange: null
        );

        var signature = AmazonCloudFrontUrlSigner.SignWithSha1RSA(
            policy,
            new StreamReader(_privateKeyPath)
        );

        return (
            AmazonCloudFrontUrlSigner.MakeBytesUrlSafe(Encoding.UTF8.GetBytes(policy)),
            AmazonCloudFrontUrlSigner.MakeBytesUrlSafe(Convert.FromBase64String(signature)),
            _keyPairId
        );
    }
}

// Controller — return signed cookie in response for video streaming
[HttpGet("stream/{videoId}")]
public async Task<IActionResult> GetStreamAccess(int videoId)
{
    // Verify user has paid access
    var hasAccess = await _subscriptionService.UserHasAccessAsync(User.GetUserId(), videoId);
    if (!hasAccess) return Forbid();

    var expiresAt = DateTime.UtcNow.AddHours(4);  // 4-hour viewing window
    var pattern = $"https://d1234.cloudfront.net/videos/{videoId}/*";

    var (policy, signature, keyPairId) = _cloudFrontSigner.GenerateSignedCookies(pattern, expiresAt);

    // Set CloudFront cookies — browser sends them automatically for subsequent .ts requests
    Response.Cookies.Append("CloudFront-Policy", policy, new CookieOptions { HttpOnly = true, Secure = true });
    Response.Cookies.Append("CloudFront-Signature", signature, new CookieOptions { HttpOnly = true, Secure = true });
    Response.Cookies.Append("CloudFront-Key-Pair-Id", keyPairId, new CookieOptions { HttpOnly = true, Secure = true });

    return Ok(new { playlistUrl = $"https://d1234.cloudfront.net/videos/{videoId}/master.m3u8" });
}
```

---

## Summary — Chapter 5

| Concept | Key Takeaway |
|---|---|
| **S3 is object storage** | Not a filesystem — objects are immutable, replaced wholesale |
| **Storage Classes** | Use Intelligent-Tiering or Lifecycle policies to reduce costs automatically |
| **Never public S3** | Always use CloudFront + OAC for serving files |
| **Pre-signed URLs** | Give temporary access to private objects without changing bucket permissions |
| **Presigned PUT URLs** | Let clients upload directly to S3 — your server stays out of the upload path |
| **Multipart Upload** | Required for files > 5GB, recommended for files > 100MB |
| **CloudFront Signed Cookies** | Best for streaming video — signs access to a whole folder, not individual files |
| **Cache Behaviors** | Match URL patterns to different TTLs — static assets: long TTL, API: no cache |
