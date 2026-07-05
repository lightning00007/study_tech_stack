using Amazon.MediaConvert;
using Amazon.MediaConvert.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace GrapeSeed.VideoService.Infrastructure.Transcoding;

// =============================================================================
// 📖 CONCEPT: AWS MediaConvert — Adaptive Bitrate Transcoding
// =============================================================================
// Raw video files uploaded by teachers can be in any format: MP4, MOV, AVI, MKV.
// Students watch on wildly different devices: 4K desktop monitors, iPhone 8,
// slow broadband connections. One-size-fits-all doesn't work.
//
// MediaConvert solves this by:
//   1. Taking the raw input file from S3.
//   2. Transcoding it to HLS (HTTP Live Streaming) format.
//   3. Creating MULTIPLE quality renditions (1080p, 720p, 360p).
//   4. Writing the output segments back to S3.
//
// HLS Adaptive Bitrate Streaming (ABR):
//   The HLS player on the student's device starts at a medium quality.
//   If the network is fast → it switches to 1080p.
//   If the network slows down → it switches to 360p without interrupting playback.
//   The player makes this decision every few seconds based on download throughput.
//
// MediaConvert is serverless — we submit a Job and MediaConvert handles the
// compute. We don't manage any EC2 instances. We're billed per minute of content.
//
// Job lifecycle:
//   SUBMITTED → PROGRESSING → COMPLETE (or ERROR)
//   MediaConvert emits CloudWatch Events at each status change.
//   We listen for COMPLETE to update the Video's status to "Ready".
//
// 🔗 SEE ALSO: docs/04-aws-services.md#44-aws-mediaconvert--video-transcoding
// 🔗 SEE ALSO: Lambda/VideoProcessingLambda.cs — triggered by S3 upload event
// =============================================================================

/// <summary>Contract for submitting MediaConvert transcoding jobs.</summary>
public interface IMediaConvertService
{
    Task<string> SubmitTranscodingJobAsync(
        string inputS3Key,
        string outputS3Prefix,
        Guid videoId,
        CancellationToken ct = default);
}

/// <summary>
/// Submits video transcoding jobs to AWS MediaConvert.
/// </summary>
public sealed class MediaConvertService : IMediaConvertService
{
    private readonly IAmazonMediaConvert _mediaConvert;
    private readonly string _roleArn;       // IAM Role that MediaConvert assumes to read/write S3
    private readonly string _bucketName;
    private readonly ILogger<MediaConvertService> _logger;

    public MediaConvertService(
        IAmazonMediaConvert mediaConvert,
        IConfiguration configuration,
        ILogger<MediaConvertService> logger)
    {
        _mediaConvert = mediaConvert;
        _roleArn = configuration["Aws:MediaConvert:RoleArn"]!;
        _bucketName = configuration["Aws:S3:VideoBucket"]!;
        _logger = logger;
    }

    public async Task<string> SubmitTranscodingJobAsync(
        string inputS3Key,
        string outputS3Prefix,
        Guid videoId,
        CancellationToken ct = default)
    {
        // 📖 CONCEPT: MediaConvert job definition
        // A Job specifies:
        //   - Input: where to read the raw video from
        //   - Outputs: what renditions to create (one output group per format)
        //   - Queue: which MediaConvert queue processes the job
        //   - Role: the IAM role MediaConvert uses to access S3
        //
        // We create one HLS Output Group with three outputs (quality levels).
        // Each output has its own resolution and bitrate settings.
        var request = new CreateJobRequest
        {
            Role = _roleArn,

            // 💡 WHY use metadata: MediaConvert stores job metadata for us.
            // When the job completes and triggers our Lambda, the metadata
            // lets us identify which GrapeSeed video the job belongs to.
            UserMetadata = new Dictionary<string, string>
            {
                ["videoId"] = videoId.ToString()
            },

            Settings = new JobSettings
            {
                Inputs =
                [
                    new Input
                    {
                        FileInput = $"s3://{_bucketName}/{inputS3Key}",
                        AudioSelectors = new Dictionary<string, AudioSelector>
                        {
                            ["Audio Selector 1"] = new AudioSelector
                            {
                                DefaultSelection = AudioDefaultSelection.DEFAULT
                            }
                        }
                    }
                ],
                OutputGroups =
                [
                    // ── HLS Output Group ─────────────────────────────────────
                    new OutputGroup
                    {
                        Name = "HLS Group",
                        OutputGroupSettings = new OutputGroupSettings
                        {
                            Type = OutputGroupType.HLS_GROUP_SETTINGS,
                            HlsGroupSettings = new HlsGroupSettings
                            {
                                Destination = $"s3://{_bucketName}/{outputS3Prefix}/",
                                SegmentLength = 6,    // 6-second HLS segments
                                MinSegmentLength = 0
                            }
                        },
                        Outputs =
                        [
                            // ── 1080p / 5 Mbps ────────────────────────────
                            BuildOutput("1080p", 1920, 1080, 5_000_000, 192_000),
                            // ── 720p / 2.5 Mbps ───────────────────────────
                            BuildOutput("720p", 1280, 720, 2_500_000, 128_000),
                            // ── 360p / 800 Kbps ───────────────────────────
                            BuildOutput("360p", 640, 360, 800_000, 96_000)
                        ]
                    }
                ]
            }
        };

        var response = await _mediaConvert.CreateJobAsync(request, ct);

        _logger.LogInformation(
            "Submitted MediaConvert job {JobId} for video {VideoId}. Status: {Status}",
            response.Job.Id, videoId, response.Job.Status);

        return response.Job.Id;
    }

    private static Output BuildOutput(string nameModifier, int width, int height, int videoBitrate, int audioBitrate)
    {
        return new Output
        {
            NameModifier = $"_{nameModifier}",   // Appended to output file name
            ContainerSettings = new ContainerSettings
            {
                Container = ContainerType.M3U8   // HLS container format
            },
            VideoDescription = new VideoDescription
            {
                Width = width,
                Height = height,
                CodecSettings = new VideoCodecSettings
                {
                    Codec = VideoCodec.H_264,
                    H264Settings = new H264Settings
                    {
                        Bitrate = videoBitrate,
                        RateControlMode = H264RateControlMode.CBR,
                        // 📖 CONCEPT: H.264 profile determines codec features/compatibility.
                        // MAIN profile is broadly compatible with all modern devices.
                        CodecProfile = H264CodecProfile.MAIN,
                        InterlaceMode = H264InterlaceMode.PROGRESSIVE
                    }
                }
            },
            AudioDescriptions =
            [
                new AudioDescription
                {
                    CodecSettings = new AudioCodecSettings
                    {
                        Codec = AudioCodec.AAC,
                        AacSettings = new AacSettings
                        {
                            Bitrate = audioBitrate,
                            SampleRate = 48000
                        }
                    }
                }
            ]
        };
    }
}
