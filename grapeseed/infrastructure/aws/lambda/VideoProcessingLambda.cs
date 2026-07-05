using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.Lambda.S3Events;
using Amazon.MediaConvert;
using Amazon.MediaConvert.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Configuration;

// Assembly attribute tells Lambda which serialiser to use for the event envelope.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace GrapeSeed.Lambda.VideoProcessing;

// =============================================================================
// 📖 CONCEPT: AWS Lambda Function
// =============================================================================
// This Lambda function is triggered when a teacher uploads a raw video to S3.
// Its job is to submit a MediaConvert transcoding job for that video.
//
// Lambda execution model:
//   - Lambda initialises the function (runs the constructor) once per "instance".
//   - Subsequent invocations on the same instance reuse the constructor state.
//   - The FunctionHandler method is called for each event.
//
// The S3Event contains information about what was uploaded:
//   - Bucket name
//   - Object key (the S3 path, e.g., "raw/{tenantId}/{videoId}/original.mp4")
//
// We extract the tenantId and videoId from the key path and submit the
// MediaConvert job, embedding the videoId as user metadata so the completion
// Lambda knows which video to update.
//
// ⚠️ GOTCHA: Lambda has a maximum execution time (15 minutes). This function
// completes in seconds (it just submits the job, not waits for it).
//
// ⚠️ GOTCHA: Lambda can be invoked multiple times for the same S3 event
// (at-least-once delivery). Always design Lambda handlers to be idempotent:
// submitting a duplicate job is harmless if we check job existence first.
//
// 🔗 SEE ALSO: infrastructure/aws/lambda/template.yaml — SAM deployment config
// 🔗 SEE ALSO: docs/04-aws-services.md#45-aws-lambda--serverless-glue-code
// =============================================================================

/// <summary>
/// Lambda function triggered by S3 ObjectCreated events for raw video uploads.
/// Submits a MediaConvert transcoding job for each uploaded video.
/// </summary>
public sealed class VideoProcessingFunction
{
    private readonly IAmazonMediaConvert _mediaConvert;
    private readonly IAmazonSimpleNotificationService _sns;
    private readonly string _mediaConvertRoleArn;
    private readonly string _bucketName;
    private readonly string _videoUploadedTopicArn;

    // 📖 CONCEPT: Constructor runs once per Lambda instance (warm container).
    // Expensive setup (SDK clients, configuration) belongs here, not in the handler.
    // Subsequent invocations on the same instance skip this expensive initialisation.
    public VideoProcessingFunction()
    {
        // 📖 CONCEPT: In Lambda, configuration comes from environment variables
        // set in the Lambda function configuration (or SAM template).
        // AWS SDK clients are initialised here and reused across invocations.
        _mediaConvert = new AmazonMediaConvertClient();
        _sns = new AmazonSimpleNotificationServiceClient();
        _mediaConvertRoleArn = Environment.GetEnvironmentVariable("MEDIACONVERT_ROLE_ARN")!;
        _bucketName = Environment.GetEnvironmentVariable("VIDEO_BUCKET_NAME")!;
        _videoUploadedTopicArn = Environment.GetEnvironmentVariable("VIDEO_UPLOADED_TOPIC_ARN")!;
    }

    /// <summary>
    /// Lambda entry point. Called once per S3 event batch.
    /// </summary>
    /// <param name="s3Event">The S3 event containing details of the uploaded file(s).</param>
    /// <param name="context">Lambda execution context (logging, remaining time, request ID).</param>
    public async Task FunctionHandler(S3Event s3Event, ILambdaContext context)
    {
        // 📖 CONCEPT: S3 events can be batched — one invocation may contain
        // multiple S3 ObjectCreated events. Process each one independently.
        foreach (var record in s3Event.Records)
        {
            await ProcessRecordAsync(record, context);
        }
    }

    private async Task ProcessRecordAsync(S3Event.S3EventNotificationRecord record, ILambdaContext context)
    {
        var s3Key = Uri.UnescapeDataString(record.S3.Object.Key.Replace("+", " "));

        context.Logger.LogInformation($"Processing S3 upload: s3://{record.S3.Bucket.Name}/{s3Key}");

        // ── Parse tenantId and videoId from the key path ───────────────────
        // Key format: "raw/{tenantId}/{videoId}/original.mp4"
        var keyParts = s3Key.Split('/');
        if (keyParts.Length < 4 || keyParts[0] != "raw")
        {
            context.Logger.LogWarning($"Unexpected S3 key format: {s3Key}. Skipping.");
            return;
        }

        if (!Guid.TryParse(keyParts[1], out var tenantId) ||
            !Guid.TryParse(keyParts[2], out var videoId))
        {
            context.Logger.LogError($"Cannot parse tenantId/videoId from key: {s3Key}. Skipping.");
            return;
        }

        // ── Submit MediaConvert transcoding job ────────────────────────────
        var outputPrefix = $"processed/{tenantId}/{videoId}";

        try
        {
            // 📖 CONCEPT: We build the same job definition here as in MediaConvertService.
            // In a real project, this logic would be shared (e.g., a NuGet package
            // containing the job definition builder). For clarity in this learning project,
            // it's slightly duplicated.
            var jobRequest = new CreateJobRequest
            {
                Role = _mediaConvertRoleArn,
                UserMetadata = new Dictionary<string, string>
                {
                    ["tenantId"] = tenantId.ToString(),
                    ["videoId"] = videoId.ToString()
                },
                Settings = new JobSettings
                {
                    Inputs =
                    [
                        new Input { FileInput = $"s3://{_bucketName}/{s3Key}" }
                    ],
                    OutputGroups =
                    [
                        new OutputGroup
                        {
                            OutputGroupSettings = new OutputGroupSettings
                            {
                                Type = OutputGroupType.HLS_GROUP_SETTINGS,
                                HlsGroupSettings = new HlsGroupSettings
                                {
                                    Destination = $"s3://{_bucketName}/{outputPrefix}/"
                                }
                            },
                            // Simplified — real job would include outputs like MediaConvertService.cs
                            Outputs = []
                        }
                    ]
                }
            };

            var response = await _mediaConvert.CreateJobAsync(jobRequest);
            context.Logger.LogInformation(
                $"MediaConvert job submitted: {response.Job.Id} for video {videoId}");

            // ── Publish VideoUploadedEvent via SNS ─────────────────────────
            // Notify the VideoService that transcoding has started.
            // VideoService will set the video's Status = Processing.
            var eventPayload = JsonSerializer.Serialize(new
            {
                EventType = "VideoUploadedEvent",
                VideoId = videoId.ToString(),
                TenantId = tenantId.ToString(),
                MediaConvertJobId = response.Job.Id,
                OccurredAt = DateTime.UtcNow
            });

            await _sns.PublishAsync(new PublishRequest
            {
                TopicArn = _videoUploadedTopicArn,
                Message = eventPayload,
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["EventType"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = "VideoUploadedEvent"
                    }
                }
            });
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Failed to process video {videoId}: {ex.Message}");
            // ⚠️ GOTCHA: Re-throwing causes Lambda to retry this batch of records
            // (if the SQS trigger has retry settings configured). This is desired
            // behaviour — we don't want to silently swallow failures.
            throw;
        }
    }
}
