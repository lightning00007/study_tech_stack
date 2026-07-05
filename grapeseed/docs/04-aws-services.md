# Chapter 4 — AWS Services

> *"The cloud is not a place. It's a way of operating."*

---

## 4.1 The Event Bus: SNS + SQS

The backbone of GrapeSeed's asynchronous communication is the SNS → SQS fan-out pattern.
Understanding it requires understanding each service individually first.

### SNS (Simple Notification Service) — The Broadcaster

SNS is a *pub/sub* system. A publisher (e.g., TenantService) sends a message to a **Topic**.
SNS immediately delivers that message to all **Subscribers** of the topic. SNS itself does not
store messages — it is fire-and-forget from the publisher's perspective.

```
TenantService ──► SNS Topic: TenantRegistered ──► delivers to all subscribers
```

### SQS (Simple Queue Service) — The Buffer

SQS is a *message queue*. Messages are stored durably until a consumer reads and deletes them.
If the consumer is temporarily down, messages accumulate in the queue and are processed when
the consumer comes back up. This is what makes the system resilient.

Each microservice that cares about `TenantRegistered` events creates its own SQS queue and
subscribes it to the SNS topic:

```
SNS Topic: TenantRegistered
    ├── SQS Queue: video-service-tenant-events    ──► VideoService consumer
    └── SQS Queue: identity-service-tenant-events ──► IdentityService consumer
```

### Why Not SNS Directly to Lambda or HTTP?

SNS can deliver directly to Lambda or HTTP endpoints. We use SQS as a buffer because:
1. **Back-pressure**: If VideoService is overwhelmed, SQS holds messages without SNS retrying.
2. **Batching**: SQS delivers up to 10 messages at once to the consumer, reducing Lambda invocations.
3. **Dead Letter Queue**: SQS can automatically route failed messages to a DLQ after N retries.

---

## 4.2 S3 — Object Storage for Videos

S3 (Simple Storage Service) stores raw video files. Teachers upload videos through the VideoService,
which in turn uploads to S3. The critical detail: **videos are never served directly from S3**.
All playback goes through CloudFront.

### Pre-Signed URLs for Secure Uploads

Rather than uploading video through the VideoService (which would proxy a potentially gigabyte-sized
file through our application server — extremely wasteful), we generate a **pre-signed URL** that
allows the client to upload directly to S3:

```
1. Client ──── POST /api/videos/upload-url ────► VideoService
2. VideoService creates a pre-signed S3 PUT URL (valid for 15 minutes)
3. VideoService ◄──── { uploadUrl: "https://s3.amazonaws.com/..." } ────
4. Client ──── PUT (binary video data) ────► S3 directly (bypasses VideoService!)
5. S3 ──── s3:ObjectCreated event ────► Lambda ──► SQS ──► VideoService
6. VideoService starts MediaConvert transcoding job
```

This pattern dramatically reduces load on the VideoService and removes a costly network hop.

### Bucket Structure

```
grapeseed-videos/
├── raw/
│   └── {tenantId}/{videoId}/original.mp4          ← uploaded by teacher
└── processed/
    └── {tenantId}/{videoId}/
        ├── playlist.m3u8                           ← HLS master playlist
        ├── 1080p/segment_000.ts
        ├── 720p/segment_000.ts
        └── 360p/segment_000.ts                     ← adaptive quality
```

---

## 4.3 CloudFront — Global CDN and Signed URLs

CloudFront is a Content Delivery Network (CDN). When a student in Vietnam streams a video,
they receive it from a CloudFront edge node in Singapore — not from the S3 bucket in us-east-1.
This dramatically reduces latency.

### Signed URLs: Time-Limited Access Control

Processed videos in S3 are **private** — they cannot be accessed without a valid CloudFront
signed URL. This means:
- A student cannot share the direct URL and let non-students watch for free.
- Access expires automatically (e.g., after 4 hours for a live class session).
- If a student's subscription is cancelled, their links stop working immediately.

```csharp
// 📖 CONCEPT: CloudFront signed URL generation
// We sign the URL using our CloudFront private key. Only GrapeSeed's servers
// have this key, so only GrapeSeed can create valid signed URLs.
// The student's browser includes this signed URL in every video segment request.
// CloudFront validates the signature before serving the segment.
var signedUrl = AmazonCloudFrontUrlSigner.GetCannedSignedURL(
    resourceUrl: $"https://cdn.grapeseed.io/processed/{tenantId}/{videoId}/playlist.m3u8",
    privateKey: _cloudFrontPrivateKey,
    keyPairId: _cloudFrontKeyPairId,
    expiresOn: DateTime.UtcNow.AddHours(4)
);
```

---

## 4.4 AWS MediaConvert — Video Transcoding

Raw videos uploaded by teachers come in dozens of formats: MP4, MOV, AVI, HEVC. Students
watch on devices with wildly different capabilities. MediaConvert solves this by transcoding
every uploaded video into **HLS (HTTP Live Streaming)** format with multiple quality levels.

HLS is an adaptive streaming protocol: the player automatically switches quality levels based
on the student's current internet speed. No more buffering because a student on a slow
connection is forced to download the 1080p version.

### The Transcoding Flow

```
1. S3 ObjectCreated event ──► Lambda (VideoProcessingLambda)
2. Lambda submits MediaConvert job:
   {
     "Input": "s3://grapeseed-videos/raw/{tenantId}/{videoId}/original.mp4",
     "Output": [
       { "Resolution": "1920x1080", "Bitrate": 5_000_000 },
       { "Resolution": "1280x720",  "Bitrate": 2_500_000 },
       { "Resolution": "640x360",   "Bitrate": 800_000  }
     ]
   }
3. MediaConvert processes (asynchronously, can take minutes)
4. MediaConvert completes ──► CloudWatch Event ──► Lambda ──► SNS ──► SQS ──► VideoService
5. VideoService updates Video.Status = Processed
6. Video appears in the tenant's library
```

---

## 4.5 AWS Lambda — Serverless Glue Code

Lambda functions are small pieces of code that run in response to events, without you
managing any servers. In GrapeSeed, Lambda is used as *glue* between AWS services:

| Lambda Function | Trigger | Action |
|---|---|---|
| `VideoProcessingLambda` | S3 ObjectCreated | Submits MediaConvert job |
| `TranscodeCompleteLambda` | CloudWatch Event from MediaConvert | Publishes SNS notification |
| `StripeWebhookLambda` | API Gateway (HTTP) | Validates Stripe signature, publishes PaymentReceived event |

Lambda is **not** used for the main service business logic — that lives in the long-running
ASP.NET Core services. Lambda is used only when the serverless model is a natural fit
(event-driven, short-lived, infrequent invocations).

### Cold Starts

A Lambda function that hasn't been invoked recently must be *initialised* before it can run.
This initialisation (loading the .NET runtime, initialising dependencies) takes 500ms–2s and
is called a **cold start**. For GrapeSeed's Lambda functions this is acceptable because they
are triggered by background events, not real-time user requests. For user-facing Lambdas,
use Provisioned Concurrency to pre-warm instances.

---

## 4.6 CloudWatch — Observability

CloudWatch is AWS's centralised observability platform. GrapeSeed uses it for:

### Structured Logging

Every service emits JSON-formatted log entries, making them queryable in CloudWatch Insights:

```json
{
  "timestamp": "2025-01-15T10:23:45Z",
  "level": "INFO",
  "service": "VideoService",
  "tenantId": "school-a",
  "requestId": "req-abc-123",
  "message": "Video transcoding job submitted",
  "videoId": "vid-001",
  "jobId": "mc-job-xyz"
}
```

With structured logs you can query: *"Show me all transcoding jobs for school-a that took more
than 10 minutes"* — something impossible with unstructured log strings.

### Custom Metrics

Beyond standard metrics (CPU, memory), GrapeSeed emits business metrics:
- `VideoWatchSessionDuration` — average time a student spends watching (engagement metric)
- `TenantRegistrationDuration` — how long the full onboarding flow takes
- `RecommendationCacheHitRate` — how often Redis serves the result vs. querying Postgres

### Alarms and Alerting

```
CloudWatch Alarm: DLQ message count > 0
        │
        ▼
SNS Topic: Alerts
        │
        ├──► Email to on-call engineer
        └──► PagerDuty (for P1 incidents)
```

---

*Continue to → [Chapter 5: EF Core and MediatR](./05-ef-core-and-mediatr.md)*
