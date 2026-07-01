# Chapter 6: AWS CloudWatch — Monitoring and Observability

---

## 6.1 What Is Observability?

Observability is the ability to understand the internal state of a system by examining its outputs. The three pillars of observability are:

| Pillar | AWS Service | Description |
|---|---|---|
| **Logs** | CloudWatch Logs | Timestamped text records of events |
| **Metrics** | CloudWatch Metrics | Numeric measurements over time |
| **Traces** | AWS X-Ray | End-to-end request tracing across services |

CloudWatch is the central hub for all three in AWS.

---

## 6.2 CloudWatch Logs

### 6.2.1 Concepts

| Concept | Description |
|---|---|
| **Log Group** | Container for log streams. Usually one per application or Lambda function. E.g., `/myapp/production` |
| **Log Stream** | A sequence of log events from a single source (one EC2 instance, one Lambda execution). |
| **Log Event** | A single log entry with a timestamp and message. |
| **Retention Policy** | How long to keep logs. Set from 1 day to 10 years (or never expire). |
| **Metric Filter** | Extract numeric values from log patterns and create CloudWatch Metrics. |

### 6.2.2 Structured Logging in .NET

The key to useful CloudWatch logs is **structured logging** — instead of plain text, you log key-value pairs that CloudWatch can index and query.

```csharp
// Install:
// dotnet add package AWS.Logger.AspNetCore
// dotnet add package Serilog.AspNetCore
// dotnet add package Serilog.Sinks.AwsCloudWatch

// appsettings.json
{
  "AWS": {
    "Region": "ap-southeast-1"
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft": "Warning",
        "System": "Warning"
      }
    }
  },
  "CloudWatch": {
    "LogGroupName": "/myapp/production"
  }
}

// Program.cs — Serilog to CloudWatch
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithProperty("Environment", builder.Environment.EnvironmentName)
    .Enrich.WithProperty("Application", "MyApp")
    .WriteTo.Console(new JsonFormatter())         // local development
    .WriteTo.AmazonCloudWatch(new CloudWatchSinkOptions
    {
        LogGroupName = "/myapp/production",
        TextFormatter = new JsonFormatter(),
        MinimumLogEventLevel = LogEventLevel.Information
    })
    .CreateLogger();

builder.Host.UseSerilog();

// In your services — structured logging
public class OrderService
{
    private readonly ILogger<OrderService> _logger;

    public async Task<Order> CreateOrderAsync(CreateOrderRequest request, CancellationToken ct)
    {
        // Structured log — creates searchable fields in CloudWatch
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["UserId"] = request.UserId,
            ["CorrelationId"] = Guid.NewGuid()
        });

        _logger.LogInformation(
            "Creating order for user {UserId} with {ItemCount} items. Total: {Total:C}",
            request.UserId, request.Items.Count, request.Total);

        try
        {
            var order = await _orderRepository.CreateAsync(request, ct);

            _logger.LogInformation(
                "Order {OrderId} created successfully for user {UserId}. Duration: {DurationMs}ms",
                order.Id, request.UserId, stopwatch.ElapsedMilliseconds);

            return order;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to create order for user {UserId}. Request: {@Request}",
                request.UserId, request);
            throw;
        }
    }
}
```

The JSON log entry in CloudWatch looks like:
```json
{
  "@t": "2024-06-15T10:30:00.000Z",
  "@l": "Information",
  "@mt": "Order {OrderId} created successfully for user {UserId}. Duration: {DurationMs}ms",
  "OrderId": 42,
  "UserId": 7,
  "DurationMs": 145,
  "Application": "MyApp",
  "Environment": "production",
  "MachineName": "ip-10-0-1-42"
}
```

### 6.2.3 CloudWatch Logs Insights — Querying Logs

Logs Insights is a powerful query language for searching and analyzing logs. It's like SQL for your logs.

```sql
-- 1. Find all ERROR logs in the last hour
fields @timestamp, @message, OrderId, UserId
| filter @message like /ERROR/ or @logLevel = 'Error'
| sort @timestamp desc
| limit 100

-- 2. Count log events by level
fields @timestamp, @logLevel
| stats count() as logCount by @logLevel
| sort logCount desc

-- 3. Find slow API requests (> 1 second)
fields @timestamp, requestPath, DurationMs, UserId
| filter DurationMs > 1000
| sort DurationMs desc
| limit 50

-- 4. Error rate per endpoint (last 24 hours)
fields @timestamp, requestPath, @logLevel
| filter @logLevel in ['Error', 'Warning']
| stats count() as errorCount by requestPath
| sort errorCount desc

-- 5. Orders created per hour (time-series analysis)
filter @mt like /Order \d+ created successfully/
| stats count() as ordersCreated by bin(1h) as hour
| sort hour asc

-- 6. Find all events for a specific user (debugging)
filter UserId = 42
| sort @timestamp asc
| display @timestamp, @logLevel, @mt, OrderId

-- 7. Average order creation time
filter @mt like /Order .* created successfully/
| stats avg(DurationMs) as avgMs, max(DurationMs) as maxMs, min(DurationMs) as minMs

-- 8. Exceptions with their stack traces
fields @timestamp, @message, @logStream
| filter @message like /Exception/
| display @timestamp, @logStream, @message
| sort @timestamp desc
```

---

## 6.3 CloudWatch Metrics

### 6.3.1 Built-in vs Custom Metrics

**Built-in Metrics** (automatic, free):
- EC2: CPUUtilization, NetworkIn, NetworkOut, DiskReadOps
- Lambda: Duration, Errors, Throttles, ConcurrentExecutions
- SQS: NumberOfMessagesSent, ApproximateAgeOfOldestMessage, NumberOfMessagesDeleted
- RDS: DatabaseConnections, FreeStorageSpace, ReadLatency, WriteLatency

**Custom Metrics** (you publish, small cost):
- Business KPIs: OrdersPlaced, RevenueGenerated, ActiveUsers
- Application performance: CacheHitRate, PaymentProcessingTime
- Queue depths: pending jobs, failed jobs

```csharp
// Install: dotnet add package AWSSDK.CloudWatch

public class BusinessMetricsService
{
    private readonly IAmazonCloudWatch _cloudWatch;
    private const string Namespace = "MyApp/Business";

    public BusinessMetricsService(IAmazonCloudWatch cloudWatch) => _cloudWatch = cloudWatch;

    public async Task RecordOrderMetricsAsync(Order order, CancellationToken ct = default)
    {
        var dimensions = new List<Dimension>
        {
            new() { Name = "Environment", Value = "production" },
            new() { Name = "Region", Value = "ap-southeast-1" }
        };

        var metrics = new List<MetricDatum>
        {
            new()
            {
                MetricName = "OrdersCreated",
                Value = 1,
                Unit = StandardUnit.Count,
                Timestamp = DateTime.UtcNow,
                Dimensions = dimensions
            },
            new()
            {
                MetricName = "OrderRevenue",
                Value = (double)order.Total,
                Unit = StandardUnit.None,
                Timestamp = DateTime.UtcNow,
                Dimensions = dimensions
            },
            new()
            {
                MetricName = "OrderProcessingTime",
                Value = order.ProcessingTimeMs,
                Unit = StandardUnit.Milliseconds,
                Timestamp = DateTime.UtcNow,
                Dimensions = dimensions
            }
        };

        await _cloudWatch.PutMetricDataAsync(new PutMetricDataRequest
        {
            Namespace = Namespace,
            MetricData = metrics
        }, ct);
    }

    public async Task RecordCacheMetricsAsync(bool isHit, string cacheType, CancellationToken ct = default)
    {
        await _cloudWatch.PutMetricDataAsync(new PutMetricDataRequest
        {
            Namespace = "MyApp/Cache",
            MetricData = new List<MetricDatum>
            {
                new()
                {
                    MetricName = isHit ? "CacheHits" : "CacheMisses",
                    Value = 1,
                    Unit = StandardUnit.Count,
                    Dimensions = new List<Dimension>
                    {
                        new() { Name = "CacheType", Value = cacheType }
                    }
                }
            }
        }, ct);
    }
}
```

### 6.3.2 CloudWatch Alarms

Alarms watch a metric and take action when a threshold is crossed.

```
Alarm States:
  OK         → metric is within normal range
  ALARM      → metric has breached the threshold
  INSUFFICIENT_DATA → not enough data points yet

Actions (triggered on state change):
  → Send SNS notification (email, SMS, pager)
  → Auto-scale EC2 instances
  → Stop/Terminate/Reboot EC2 instances
  → Execute Lambda function
```

```csharp
public async Task CreateErrorRateAlarmAsync()
{
    // Alarm: Lambda error rate > 5% over 5 minutes
    await _cloudWatch.PutMetricAlarmAsync(new PutMetricAlarmRequest
    {
        AlarmName = "MyApp-Lambda-HighErrorRate",
        AlarmDescription = "Lambda function error rate exceeded 5%",
        MetricName = "Errors",
        Namespace = "AWS/Lambda",
        Dimensions = new List<Dimension>
        {
            new() { Name = "FunctionName", Value = "my-order-processor" }
        },
        Statistic = Statistic.Sum,
        Period = 300,           // 5 minutes (300 seconds)
        EvaluationPeriods = 2,  // 2 consecutive periods must breach
        Threshold = 5.0,
        ComparisonOperator = ComparisonOperator.GreaterThanThreshold,
        TreatMissingData = "notBreaching",  // no data = OK (function just isn't called)
        AlarmActions = new List<string>
        {
            "arn:aws:sns:ap-southeast-1:123456789:engineering-alerts"
        },
        OKActions = new List<string>
        {
            "arn:aws:sns:ap-southeast-1:123456789:engineering-alerts"
        }
    });

    // Alarm: SQS message age > 5 minutes (processing is too slow or consumers are down)
    await _cloudWatch.PutMetricAlarmAsync(new PutMetricAlarmRequest
    {
        AlarmName = "MyApp-SQS-OldMessages",
        MetricName = "ApproximateAgeOfOldestMessage",
        Namespace = "AWS/SQS",
        Dimensions = new List<Dimension>
        {
            new() { Name = "QueueName", Value = "order-events.fifo" }
        },
        Statistic = Statistic.Maximum,
        Period = 60,
        EvaluationPeriods = 5,
        Threshold = 300,        // 300 seconds = 5 minutes
        ComparisonOperator = ComparisonOperator.GreaterThanThreshold,
        AlarmActions = new List<string>
        {
            "arn:aws:sns:ap-southeast-1:123456789:engineering-alerts"
        }
    });
}
```

---

## 6.4 AWS X-Ray — Distributed Tracing

X-Ray traces requests as they flow through multiple services. It shows you exactly where time is spent.

```
A single user request:
[API Gateway: 2ms] → [Lambda: 450ms]
                           ↓
                    [DynamoDB Query: 12ms]
                    [S3 GetObject: 180ms]
                    [SQS SendMessage: 8ms]
                    [External API call: 240ms]  ← SLOW! This is the bottleneck
```

```csharp
// Install: dotnet add package AWSXRayRecorder.Core
//          dotnet add package AWSXRayRecorder.Handlers.AspNetCore

// Program.cs
app.UseXRay("MyApp");  // must be before other middleware

// In service code — add custom subsegments for fine-grained tracing
public async Task<Order> ProcessOrderAsync(int orderId, CancellationToken ct)
{
    return await AWSXRayRecorder.Instance.TraceMethodAsync("ProcessOrder", async () =>
    {
        // Add annotations (indexed — searchable in X-Ray console)
        AWSXRayRecorder.Instance.AddAnnotation("OrderId", orderId);
        AWSXRayRecorder.Instance.AddAnnotation("Environment", "production");

        // Add metadata (not indexed, for debugging)
        AWSXRayRecorder.Instance.AddMetadata("OrderDetails", new { orderId, timestamp = DateTime.UtcNow });

        var order = await GetOrderAsync(orderId, ct);

        // Create a subsegment for a logical operation
        using (AWSXRayRecorder.Instance.BeginSubsegment("CalculateTax"))
        {
            order.Tax = await CalculateTaxAsync(order);
        }

        return order;
    });
}
```

---

# Chapter 7: AWS Media Services — Video Processing and Streaming

---

## 7.1 The Challenge of Video

Video is fundamentally different from other files:
- **Large files**: A 1-hour 1080p video is 4-8 GB raw
- **Multiple quality levels**: Users on different connections need different bitrates
- **Adaptive streaming**: Players switch quality levels dynamically based on bandwidth
- **Format compatibility**: Different browsers and devices support different codecs

AWS provides a suite of managed services that solve these challenges.

---

## 7.2 The Complete VOD (Video-On-Demand) Pipeline

```
1. User uploads raw video to S3 (source bucket)
         ↓
2. S3 Event notification triggers Lambda
         ↓
3. Lambda submits a MediaConvert job
         ↓
4. MediaConvert transcodes video into HLS and DASH formats
   - Creates multiple quality levels (1080p, 720p, 480p, 360p)
   - Creates .m3u8 playlists (HLS) and .mpd manifests (DASH)
   - Creates .ts segments (HLS) and .mp4 segments (DASH)
         ↓
5. MediaConvert writes output to S3 (output bucket)
         ↓
6. MediaConvert sends completion event to EventBridge
         ↓
7. Lambda handles completion: update database, notify user
         ↓
8. CloudFront CDN serves the HLS/DASH content
         ↓
9. User's browser: HLS.js or Video.js plays adaptive video
```

---

## 7.3 HLS and DASH — Adaptive Streaming Formats

**HLS (HTTP Live Streaming)** — Apple's format, now universal:
```
master.m3u8              ← master playlist (lists all quality levels)
├── 1080p/stream.m3u8    ← 1080p playlist (lists all segments for this quality)
│   ├── segment_001.ts   ← 6-second video segment
│   ├── segment_002.ts
│   └── ...
├── 720p/stream.m3u8
│   ├── segment_001.ts
│   └── ...
└── 360p/stream.m3u8
```

**DASH (Dynamic Adaptive Streaming over HTTP)** — industry standard:
```
manifest.mpd             ← manifest file
├── video_1080p/         ← 1080p segments
├── video_720p/
└── audio/
```

The player (HLS.js/Video.js) monitors download speed and automatically switches between quality levels every few segments.

---

## 7.4 AWS MediaConvert — Transcoding Service

MediaConvert converts video files between formats. You define a **Job** with input, output groups, and encoding settings.

### Complete MediaConvert Job in .NET

```csharp
// Install: dotnet add package AWSSDK.MediaConvert

public class VideoTranscodeService
{
    private readonly IAmazonMediaConvert _mediaConvert;
    private readonly string _roleArn;
    private const string SourceBucket = "my-source-videos";
    private const string OutputBucket = "my-output-videos";

    public VideoTranscodeService(IAmazonMediaConvert mediaConvert, IConfiguration config)
    {
        _mediaConvert = mediaConvert;
        _roleArn = config["AWS:MediaConvertRoleArn"]!;
    }

    public async Task<string> CreateHlsTranscodeJobAsync(string sourceKey, string videoId, CancellationToken ct = default)
    {
        var request = new CreateJobRequest
        {
            Role = _roleArn,
            Settings = new JobSettings
            {
                TimecodeConfig = new TimecodeConfig { Source = TimecodeSource.ZEROBASED },
                Inputs = new List<Input>
                {
                    new Input
                    {
                        FileInput = $"s3://{SourceBucket}/{sourceKey}",
                        AudioSelectors = new Dictionary<string, AudioSelector>
                        {
                            ["Audio Selector 1"] = new AudioSelector { DefaultSelection = AudioDefaultSelection.DEFAULT }
                        },
                        VideoSelector = new VideoSelector()
                    }
                },
                OutputGroups = new List<OutputGroup>
                {
                    // HLS Output Group — creates master.m3u8 + quality playlists + .ts segments
                    new OutputGroup
                    {
                        Name = "HLS Output",
                        OutputGroupSettings = new OutputGroupSettings
                        {
                            Type = OutputGroupType.HLS_GROUP_SETTINGS,
                            HlsGroupSettings = new HlsGroupSettings
                            {
                                Destination = $"s3://{OutputBucket}/videos/{videoId}/hls/",
                                SegmentLength = 6,          // 6-second segments
                                MinSegmentLength = 0,
                                DirectoryStructure = HlsDirectoryStructure.SINGLE_DIRECTORY,
                                ManifestDurationFormat = HlsManifestDurationFormat.INTEGER,
                                StreamInfResolution = HlsStreamInfResolution.INCLUDE,
                                // Encryption (DRM)
                                // Encryption = new HlsEncryptionSettings { ... }
                            }
                        },
                        Outputs = new List<Output>
                        {
                            // 1080p — Full HD
                            CreateHlsVideoOutput("_1080p", 1920, 1080, 5_000_000, "_audio"),

                            // 720p — HD
                            CreateHlsVideoOutput("_720p", 1280, 720, 3_000_000, "_audio"),

                            // 480p — SD
                            CreateHlsVideoOutput("_480p", 854, 480, 1_500_000, "_audio"),

                            // 360p — Low bandwidth
                            CreateHlsVideoOutput("_360p", 640, 360, 800_000, "_audio"),

                            // Audio-only track
                            new Output
                            {
                                NameModifier = "_audio",
                                AudioDescriptions = new List<AudioDescription>
                                {
                                    new AudioDescription
                                    {
                                        AudioSourceName = "Audio Selector 1",
                                        CodecSettings = new AudioCodecSettings
                                        {
                                            Codec = AudioCodec.AAC,
                                            AacSettings = new AacSettings
                                            {
                                                SampleRate = 48000,
                                                Bitrate = 128000,
                                                CodingMode = AacCodingMode.CODING_MODE_2_0
                                            }
                                        }
                                    }
                                },
                                ContainerSettings = new ContainerSettings { Container = ContainerType.M3U8 }
                            }
                        }
                    },

                    // Thumbnail Output Group — generates preview images
                    new OutputGroup
                    {
                        Name = "Thumbnails",
                        OutputGroupSettings = new OutputGroupSettings
                        {
                            Type = OutputGroupType.FILE_GROUP_SETTINGS,
                            FileGroupSettings = new FileGroupSettings
                            {
                                Destination = $"s3://{OutputBucket}/videos/{videoId}/thumbnails/"
                            }
                        },
                        Outputs = new List<Output>
                        {
                            new Output
                            {
                                NameModifier = "_thumb",
                                VideoDescription = new VideoDescription
                                {
                                    Width = 1280, Height = 720,
                                    CodecSettings = new VideoCodecSettings
                                    {
                                        Codec = VideoCodec.FRAME_CAPTURE,
                                        FrameCaptureSettings = new FrameCaptureSettings
                                        {
                                            FramerateNumerator = 1,
                                            FramerateDenominator = 10, // 1 frame every 10 seconds
                                            MaxCaptures = 20,
                                            Quality = 80
                                        }
                                    }
                                },
                                ContainerSettings = new ContainerSettings { Container = ContainerType.RAW }
                            }
                        }
                    }
                }
            },
            // Job tags for cost allocation
            Tags = new Dictionary<string, string>
            {
                ["VideoId"] = videoId,
                ["Environment"] = "production"
            },
            // Notifications via EventBridge (recommended over polling)
            UserMetadata = new Dictionary<string, string>
            {
                ["VideoId"] = videoId,
                ["SourceKey"] = sourceKey
            }
        };

        var response = await _mediaConvert.CreateJobAsync(request, ct);
        return response.Job.Id;
    }

    private static Output CreateHlsVideoOutput(string nameSuffix, int width, int height, int bitrate, string audioTrack)
    {
        return new Output
        {
            NameModifier = nameSuffix,
            VideoDescription = new VideoDescription
            {
                Width = width,
                Height = height,
                CodecSettings = new VideoCodecSettings
                {
                    Codec = VideoCodec.H_264,
                    H264Settings = new H264Settings
                    {
                        Bitrate = bitrate,
                        RateControlMode = H264RateControlMode.CBR,
                        CodecLevel = H264CodecLevel.AUTO,
                        CodecProfile = H264CodecProfile.MAIN,
                        EntropyEncoding = H264EntropyEncoding.CABAC,
                        FramerateControl = H264FramerateControl.INITIALIZE_FROM_SOURCE,
                        GopSize = 2.0,              // 2-second GOP (keyframe interval)
                        GopSizeUnits = H264GopSizeUnits.SECONDS,
                        NumberBFramesBetweenReferenceFrames = 2,
                        QualityTuningLevel = H264QualityTuningLevel.MULTI_PASS_HQ
                    }
                }
            },
            AudioDescriptions = new List<AudioDescription>
            {
                new AudioDescription
                {
                    AudioSourceName = "Audio Selector 1",
                    CodecSettings = new AudioCodecSettings
                    {
                        Codec = AudioCodec.AAC,
                        AacSettings = new AacSettings { Bitrate = 128000, SampleRate = 48000, CodingMode = AacCodingMode.CODING_MODE_2_0 }
                    }
                }
            },
            ContainerSettings = new ContainerSettings
            {
                Container = ContainerType.M3U8,
                M3u8Settings = new M3u8Settings { AudioFramesPerPes = 4 }
            }
        };
    }

    // Check job status
    public async Task<string> GetJobStatusAsync(string jobId, CancellationToken ct = default)
    {
        var response = await _mediaConvert.GetJobAsync(new GetJobRequest { Id = jobId }, ct);
        return response.Job.Status.Value;
        // Possible values: SUBMITTED, PROGRESSING, COMPLETE, CANCELED, ERROR
    }
}
```

### MediaConvert Completion Event Handler (EventBridge → Lambda)

```csharp
// Lambda triggered by EventBridge when MediaConvert job completes
public class MediaConvertCompletionHandler
{
    public async Task HandleAsync(CloudWatchEvent<MediaConvertJobStateChange> cloudWatchEvent, ILambdaContext context)
    {
        var detail = cloudWatchEvent.Detail;
        context.Logger.LogInformation("MediaConvert job {JobId} status: {Status}", detail.JobId, detail.Status);

        if (detail.Status == "COMPLETE")
        {
            var videoId = detail.UserMetadata["VideoId"];
            var outputUri = detail.OutputGroupDetails.First().OutputDetails.First().OutputFilePaths.First();

            // Update database: mark video as ready
            await _videoRepository.MarkAsReadyAsync(videoId, outputUri);

            // Notify user: "Your video is ready!"
            await _notificationService.SendVideoReadyNotificationAsync(videoId);
        }
        else if (detail.Status == "ERROR")
        {
            var errorCode = detail.ErrorCode;
            context.Logger.LogError("MediaConvert job failed. Error: {ErrorCode}", errorCode);
            await _videoRepository.MarkAsFailedAsync(detail.UserMetadata["VideoId"], errorCode.ToString());
        }
    }
}
```

---

## 7.5 MediaLive vs MediaConvert — Key Difference

| | MediaConvert | MediaLive |
|---|---|---|
| **Use case** | Video-on-Demand (VOD) — recorded videos | Live streaming — real-time broadcasts |
| **Input** | File in S3 | Live stream (RTMP, RTSP, SDI) |
| **Output** | Files in S3 | MediaPackage, S3, or MediaStore |
| **Pricing** | Per minute of output video processed | Per running channel (even if idle) |
| **Start time** | Seconds | Minutes (channel must be running) |

**For a typical app (upload → transcode → stream)**: Use **MediaConvert**. MediaLive is for sports broadcasts, live events, 24/7 channels.

---

# Chapter 8: AWS Lambda — Serverless Compute

---

## 8.1 What Is Serverless?

"Serverless" does not mean there are no servers. It means **you don't manage servers**. AWS handles provisioning, scaling, patching, and capacity planning. You write a function, deploy it, and AWS runs it.

Lambda is AWS's serverless compute service. You write a function handler, package it, and Lambda runs it in response to **events**.

**Pricing**: $0.0000166667 per GB-second of execution, plus $0.20 per 1 million requests. The first 1 million requests per month are free.

---

## 8.2 Lambda Execution Model

### 8.2.1 Execution Environment Lifecycle

```
Request arrives:
    │
    ├─ Is a warm execution environment available?
    │       ├─ YES (Warm Start): invoke function immediately ← ~1ms overhead
    │       │
    │       └─ NO (Cold Start): spin up new environment
    │               ↓
    │         1. Download your deployment package
    │         2. Start the container/microVM (Firecracker)
    │         3. Initialize the .NET runtime
    │         4. Run your module-level initialization code (static constructors, DI setup)
    │         5. Run your handler function
    │              ← Cold start overhead: 500ms–2000ms for .NET
    │
    └─ Function executes
    └─ Environment kept alive for ~15 minutes
    └─ If no new requests in ~15 minutes → environment frozen/terminated
```

### 8.2.2 Cold Start Mitigation

Cold starts are the main performance concern with Lambda, especially for .NET:

**Strategies:**
1. **Provisioned Concurrency**: Keep N execution environments pre-warmed (costs money, but guarantees no cold starts for those instances)
2. **SnapStart** (.NET 8+): Pre-initialize the .NET runtime and take a snapshot — dramatically reduces cold start time
3. **Reduce package size**: Smaller zip = faster download = faster cold start
4. **Use top-level statements**: In .NET, minimize static initialization code
5. **AOT Compilation** (Native AOT): Compile to native binary — cold starts < 100ms, but has limitations

```csharp
// Lambda function with SnapStart optimization
// Published as Native AOT:
// dotnet publish -c Release -r linux-x64 --self-contained

// Minimal Lambda function (fast cold start)
public class Function
{
    // Initialize outside handler — runs once, reused across invocations
    private static readonly HttpClient _httpClient = new();
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    // The handler must be registered in lambda function configuration:
    // Handler: MyAssembly::MyNamespace.Function::FunctionHandler
    public async Task<string> FunctionHandler(string input, ILambdaContext context)
    {
        context.Logger.LogInformation("Processing: {Input}", input);
        // ... logic
        return "done";
    }
}
```

---

## 8.3 Lambda Triggers — What Can Invoke a Lambda

### 8.3.1 API Gateway Trigger (HTTP)

```csharp
// Install: dotnet add package Amazon.Lambda.APIGatewayEvents

public class ApiHandler
{
    public async Task<APIGatewayProxyResponse> HandleAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        context.Logger.LogInformation("HTTP {Method} {Path}", request.HttpMethod, request.Path);

        try
        {
            var id = request.PathParameters?["id"];
            var queryParam = request.QueryStringParameters?.GetValueOrDefault("filter");
            var body = request.Body;
            var authHeader = request.Headers?.GetValueOrDefault("Authorization");

            var result = await ProcessRequestAsync(id, queryParam);

            return new APIGatewayProxyResponse
            {
                StatusCode = 200,
                Body = JsonSerializer.Serialize(result),
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json",
                    ["X-Request-Id"] = context.AwsRequestId
                }
            };
        }
        catch (NotFoundException ex)
        {
            return new APIGatewayProxyResponse
            {
                StatusCode = 404,
                Body = JsonSerializer.Serialize(new { error = ex.Message })
            };
        }
        catch (Exception ex)
        {
            context.Logger.LogError(ex, "Unhandled error");
            return new APIGatewayProxyResponse
            {
                StatusCode = 500,
                Body = JsonSerializer.Serialize(new { error = "Internal server error" })
            };
        }
    }
}
```

### 8.3.2 S3 Trigger

```csharp
// Install: dotnet add package Amazon.Lambda.S3Events

public class S3EventHandler
{
    private readonly VideoTranscodeService _transcodeService;

    public async Task HandleAsync(S3Event s3Event, ILambdaContext context)
    {
        foreach (var record in s3Event.Records)
        {
            var bucket = record.S3.Bucket.Name;
            var key = Uri.UnescapeDataString(record.S3.Object.Key.Replace("+", " "));
            var size = record.S3.Object.Size;
            var eventName = record.EventName;  // e.g., "ObjectCreated:Put"

            context.Logger.LogInformation(
                "S3 event: {EventName} - s3://{Bucket}/{Key} ({Size} bytes)",
                eventName, bucket, key, size);

            if (eventName.StartsWith("ObjectCreated") && IsVideoFile(key))
            {
                var videoId = ExtractVideoId(key);
                await _transcodeService.CreateHlsTranscodeJobAsync(key, videoId);
                context.Logger.LogInformation("Transcoding started for video {VideoId}", videoId);
            }
        }
    }

    private static bool IsVideoFile(string key) =>
        new[] { ".mp4", ".mov", ".avi", ".mkv", ".wmv" }
            .Any(ext => key.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
}
```

### 8.3.3 SQS Trigger

```csharp
// Install: dotnet add package Amazon.Lambda.SQSEvents

public class SqsEventHandler
{
    private readonly IOrderProcessor _orderProcessor;

    public async Task<SQSBatchResponse> HandleAsync(SQSEvent sqsEvent, ILambdaContext context)
    {
        var batchItemFailures = new List<SQSBatchResponse.BatchItemFailure>();

        foreach (var message in sqsEvent.Records)
        {
            try
            {
                context.Logger.LogInformation("Processing SQS message {MessageId}", message.MessageId);

                var order = JsonSerializer.Deserialize<OrderEvent>(message.Body)
                    ?? throw new InvalidOperationException("Could not parse message body");

                await _orderProcessor.ProcessAsync(order);

                context.Logger.LogInformation("Message {MessageId} processed successfully", message.MessageId);
                // Successful messages are automatically deleted by Lambda/SQS integration
            }
            catch (Exception ex)
            {
                context.Logger.LogError(ex, "Failed to process message {MessageId}", message.MessageId);

                // Report this message as a failure
                // Lambda will NOT delete it — SQS will retry after VisibilityTimeout
                // After maxReceiveCount retries → moves to DLQ
                batchItemFailures.Add(new SQSBatchResponse.BatchItemFailure
                {
                    ItemIdentifier = message.MessageId
                });
            }
        }

        return new SQSBatchResponse { BatchItemFailures = batchItemFailures };
    }
}
```

### 8.3.4 EventBridge Scheduled Trigger (Cron)

```csharp
// Install: dotnet add package Amazon.Lambda.CloudWatchEvents

public class ScheduledTaskHandler
{
    private readonly IReportService _reportService;

    // EventBridge rule: cron(0 0 * * ? *) = every day at midnight UTC
    public async Task HandleAsync(ScheduledEvent scheduledEvent, ILambdaContext context)
    {
        context.Logger.LogInformation(
            "Scheduled task triggered at {Time}. Account: {Account}",
            scheduledEvent.Time,
            scheduledEvent.Account);

        await _reportService.GenerateDailyReportAsync(DateTime.UtcNow.AddDays(-1));

        context.Logger.LogInformation("Daily report generated successfully");
    }
}
```

---

## 8.4 Lambda Concurrency and Scaling

### 8.4.1 Scaling Behavior

```
Traffic spike: 100 simultaneous requests

Lambda behavior:
  Request 1: uses existing warm instance
  Request 2: uses another warm instance
  Requests 3-100: Lambda spins up new instances (burst scaling)

Lambda can scale from 0 to 3000 concurrent executions in seconds.
After 3000: scales at 500 additional per minute.

Default Account Limit: 1000 concurrent executions per region (soft limit, increasable)
```

### 8.4.2 Concurrency Types

```csharp
// Unreserved Concurrency
// → Shares from account's 1000-unit pool with all other Lambda functions

// Reserved Concurrency
// → Guarantees N executions ALWAYS available for this function
// → Also LIMITS this function to at most N concurrent (throttle protection for DB)
// Set via console or:
await _lambda.PutFunctionConcurrencyAsync(new PutFunctionConcurrencyRequest
{
    FunctionName = "my-order-processor",
    ReservedConcurrentExecutions = 50  // max 50 concurrent, always available
});

// Provisioned Concurrency
// → Keep N execution environments pre-initialized (eliminates cold starts)
// → Costs money even when idle
await _lambda.PutProvisionedConcurrencyConfigAsync(new PutProvisionedConcurrencyConfigRequest
{
    FunctionName = "my-api-handler",
    Qualifier = "production",  // function alias
    ProvisionedConcurrentExecutions = 10
});
```

### 8.4.3 Lambda Best Practices Summary

```csharp
public class BestPracticeLambdaFunction
{
    // ✅ GOOD: Initialize once outside handler (reused across warm invocations)
    private static readonly HttpClient _httpClient = new();
    private static readonly IAmazonS3 _s3 = new AmazonS3Client();

    // ❌ BAD: Creating new HttpClient per invocation = connection exhaustion
    // public async Task Handler(...) { var client = new HttpClient(); ... }

    public async Task<string> Handler(SQSEvent input, ILambdaContext context)
    {
        // ✅ Always log with structured data for CloudWatch Logs Insights
        context.Logger.LogInformation("Processing {Count} messages", input.Records.Count);

        // ✅ Check remaining time to gracefully handle timeout
        if (context.RemainingTime < TimeSpan.FromSeconds(10))
        {
            context.Logger.LogWarning("Less than 10 seconds remaining! Gracefully stopping.");
            return "timeout-imminent";
        }

        // ✅ Use CancellationToken tied to Lambda timeout
        using var cts = new CancellationTokenSource(context.RemainingTime - TimeSpan.FromSeconds(5));

        foreach (var message in input.Records)
        {
            await ProcessMessageAsync(message.Body, cts.Token);
        }

        return "success";
    }
}
```

---

## Summary — Chapters 6, 7, 8

### CloudWatch
- **Logs Insights** is your debugging superpower — learn its query syntax
- **Custom metrics** track business KPIs, not just infrastructure
- **Alarms** should alert on business impact: DLQ depth, error rate, SQS age

### Media Services
- **MediaConvert** = VOD transcoding. Submit a job, it handles the rest.
- **HLS** is the output format — master playlist + quality playlists + 6-second segments
- **EventBridge** + Lambda handles completion notifications (never poll)

### Lambda
- **Cold starts** are the main challenge — use SnapStart, Provisioned Concurrency, or Native AOT
- **Initialize outside the handler** — static clients are reused across warm invocations
- **Batch processing with partial failures** — report individual failures, successful items are auto-deleted
- **Reserved Concurrency** protects your DB from Lambda traffic spikes
- **15-minute timeout** — Lambda should trigger work (MediaConvert, Step Functions), not run it
