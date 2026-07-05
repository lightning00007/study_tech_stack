namespace GrapeSeed.VideoService.Domain;

// =============================================================================
// 📖 CONCEPT: Video Aggregate Root
// =============================================================================
// In VideoService's bounded context, a Video is a piece of content with:
//   - A lifecycle: Uploading → Processing → Ready (or Failed)
//   - Storage locations: S3 key for the raw file, CloudFront key for streaming
//   - Metadata: title, description, duration, tenant ownership
//
// Unlike TenantService where Tenant has complex business rules, Video is
// relatively simple — most of its "intelligence" lives in the AWS services
// (S3 for storage, MediaConvert for transcoding, CloudFront for delivery).
// The Video entity just tracks the state machine.
// =============================================================================

/// <summary>Represents the processing lifecycle of a video asset.</summary>
public enum VideoStatus
{
    /// <summary>Teacher has requested upload. Raw file not yet uploaded to S3.</summary>
    Uploading,
    /// <summary>Raw file uploaded to S3. Awaiting MediaConvert transcoding.</summary>
    Processing,
    /// <summary>Transcoding complete. Video is available for student streaming.</summary>
    Ready,
    /// <summary>Transcoding failed. Manual intervention required.</summary>
    Failed
}

/// <summary>
/// Represents a video asset within a tenant's content library.
/// Tracks the full lifecycle from upload intent to streaming availability.
/// </summary>
public sealed class Video
{
    public Guid Id { get; private init; }
    public Guid TenantId { get; private init; }
    public string Title { get; private set; } = string.Empty;
    public string? Description { get; private set; }

    /// <summary>S3 key for the raw uploaded file (e.g., "raw/{tenantId}/{videoId}/original.mp4").</summary>
    public string RawS3Key { get; private init; } = string.Empty;

    /// <summary>S3 prefix for the processed HLS output. Null until transcoding completes.</summary>
    public string? ProcessedS3Prefix { get; private set; }

    /// <summary>AWS MediaConvert Job ID. Set when transcoding starts.</summary>
    public string? MediaConvertJobId { get; private set; }

    public VideoStatus Status { get; private set; }
    public TimeSpan? Duration { get; private set; }
    public DateTime CreatedAt { get; private init; }
    public DateTime? ReadyAt { get; private set; }

    // EF Core constructor
    private Video() { }

    public Video(Guid id, Guid tenantId, string title, string? description, string rawS3Key)
    {
        Id = id;
        TenantId = tenantId;
        Title = title;
        Description = description;
        RawS3Key = rawS3Key;
        Status = VideoStatus.Uploading;
        CreatedAt = DateTime.UtcNow;
    }

    /// <summary>Called when MediaConvert job is submitted. Transitions Uploading → Processing.</summary>
    public void MarkAsProcessing(string mediaConvertJobId)
    {
        if (Status != VideoStatus.Uploading)
            throw new InvalidOperationException($"Cannot start processing from status '{Status}'.");
        Status = VideoStatus.Processing;
        MediaConvertJobId = mediaConvertJobId;
    }

    /// <summary>Called when MediaConvert job completes. Transitions Processing → Ready.</summary>
    public void MarkAsReady(string processedS3Prefix, TimeSpan duration)
    {
        if (Status != VideoStatus.Processing)
            throw new InvalidOperationException($"Cannot mark ready from status '{Status}'.");
        Status = VideoStatus.Ready;
        ProcessedS3Prefix = processedS3Prefix;
        Duration = duration;
        ReadyAt = DateTime.UtcNow;
    }

    /// <summary>Called when MediaConvert job fails. Transitions Processing → Failed.</summary>
    public void MarkAsFailed()
    {
        Status = VideoStatus.Failed;
    }
}
