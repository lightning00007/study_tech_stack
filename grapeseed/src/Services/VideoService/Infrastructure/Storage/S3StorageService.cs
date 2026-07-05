using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace GrapeSeed.VideoService.Infrastructure.Storage;

// =============================================================================
// 📖 CONCEPT: S3 Storage Service
// =============================================================================
// This service handles all S3 interactions for the VideoService.
// It provides two main capabilities:
//
//   1. Pre-signed Upload URLs: Allows clients to upload directly to S3 without
//      routing large files through our application server.
//
//   2. S3 object management: Checking existence, listing, and deletion.
//
// Bucket layout:
//   grapeseed-videos/
//   ├── raw/{tenantId}/{videoId}/original.{ext}      ← raw uploads from teachers
//   └── processed/{tenantId}/{videoId}/
//       ├── playlist.m3u8                             ← HLS master playlist
//       └── {quality}/segment_{n}.ts                 ← video segments
//
// Security model:
//   - The bucket is private (no public access).
//   - Raw uploads: pre-signed PUT URLs (teacher uploads directly, 15 min window).
//   - Processed videos: CloudFront signed URLs (student streaming, 4 hour window).
//   - No direct S3 access for students — always via CloudFront.
//
// 🔗 SEE ALSO: CloudFrontSignedUrlService.cs — streaming URL generation
// 🔗 SEE ALSO: docs/04-aws-services.md#42-s3--object-storage-for-videos
// =============================================================================

/// <summary>Contract for S3 storage operations.</summary>
public interface IS3StorageService
{
    Task<string> GenerateUploadUrlAsync(string s3Key, TimeSpan expiry, CancellationToken ct = default);
    Task<bool> ObjectExistsAsync(string s3Key, CancellationToken ct = default);
    Task DeleteObjectAsync(string s3Key, CancellationToken ct = default);
}

/// <summary>
/// AWS S3 implementation. Uses the AWS SDK v3 for .NET.
/// </summary>
public sealed class S3StorageService : IS3StorageService
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucketName;
    private readonly ILogger<S3StorageService> _logger;

    public S3StorageService(IAmazonS3 s3, IConfiguration configuration, ILogger<S3StorageService> logger)
    {
        _s3 = s3;
        _bucketName = configuration["Aws:S3:VideoBucket"]
            ?? throw new InvalidOperationException("Aws:S3:VideoBucket configuration is required.");
        _logger = logger;
    }

    /// <summary>
    /// Generates a pre-signed S3 PUT URL, allowing the client to upload a file directly to S3.
    /// The URL is valid only for the specified duration.
    /// </summary>
    /// <param name="s3Key">The S3 object key (path within the bucket).</param>
    /// <param name="expiry">How long the upload URL should remain valid.</param>
    public async Task<string> GenerateUploadUrlAsync(string s3Key, TimeSpan expiry, CancellationToken ct = default)
    {
        // 📖 CONCEPT: Pre-signed URL generation
        // GetPreSignedURL creates a URL that includes authentication credentials
        // embedded as query parameters. The holder of this URL can PUT an object
        // to the specified S3 key without needing AWS credentials themselves.
        //
        // The AWSSDK v3 uses GetPreSignedUrlRequest to configure:
        //   - Bucket and Key (the destination)
        //   - Verb (PUT for uploads)
        //   - Expires (when the URL stops working)
        //   - ContentType constraint (optional: rejects wrong file types)
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucketName,
            Key = s3Key,
            Verb = HttpVerb.PUT,
            Expires = DateTime.UtcNow.Add(expiry),

            // 💡 WHY: Constraining ContentType prevents someone from uploading
            // executable files or malicious scripts using a video upload URL.
            ContentType = "video/*"
        };

        var url = await _s3.GetPreSignedURLAsync(request);
        _logger.LogDebug("Generated pre-signed upload URL for key {S3Key}, valid for {Expiry}", s3Key, expiry);
        return url;
    }

    public async Task<bool> ObjectExistsAsync(string s3Key, CancellationToken ct = default)
    {
        try
        {
            // GetObjectMetadata returns metadata without downloading the object body.
            // If the object doesn't exist, it throws AmazonS3Exception with 404.
            await _s3.GetObjectMetadataAsync(_bucketName, s3Key, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task DeleteObjectAsync(string s3Key, CancellationToken ct = default)
    {
        await _s3.DeleteObjectAsync(_bucketName, s3Key, ct);
        _logger.LogInformation("Deleted S3 object {S3Key}", s3Key);
    }

    /// <summary>
    /// Helper: constructs the S3 key for a raw uploaded video.
    /// Centralises key generation to prevent inconsistencies across the codebase.
    /// </summary>
    public static string BuildRawVideoKey(Guid tenantId, Guid videoId, string fileExtension)
        => $"raw/{tenantId}/{videoId}/original{fileExtension}";

    /// <summary>Helper: constructs the S3 key prefix for processed HLS output.</summary>
    public static string BuildProcessedVideoPrefix(Guid tenantId, Guid videoId)
        => $"processed/{tenantId}/{videoId}";
}
