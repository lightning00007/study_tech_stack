using Amazon.CloudFront;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace GrapeSeed.VideoService.Infrastructure.Cdn;

// =============================================================================
// 📖 CONCEPT: CloudFront Signed URL Service
// =============================================================================
// CloudFront is Amazon's Content Delivery Network (CDN). Videos stored in S3
// are served to students through CloudFront for two reasons:
//
//   1. PERFORMANCE: CloudFront has edge nodes distributed globally. A student
//      in Singapore streams from the Singapore edge node, not from an S3 bucket
//      in us-east-1. This dramatically reduces latency and buffering.
//
//   2. SECURITY: The S3 bucket is private. Students can never access it directly.
//      They can only stream videos via time-limited CloudFront Signed URLs.
//
// How Signed URLs work:
//   1. Student presses Play in the browser.
//   2. Browser calls GET /api/videos/{id}/stream-url (requires valid JWT).
//   3. VideoService verifies the student has access to this video (tenant match).
//   4. VideoService generates a CloudFront Signed URL valid for 4 hours.
//   5. VideoService returns the signed URL to the browser.
//   6. Browser's HLS player requests video segments from CloudFront using the signed URL.
//   7. CloudFront validates the signature before delivering each segment.
//
// The signed URL is constructed using:
//   - The CloudFront distribution domain (e.g., d1abc123.cloudfront.net)
//   - The S3 key of the HLS playlist (processed/{tenantId}/{videoId}/playlist.m3u8)
//   - A canned policy specifying the URL and expiry
//   - RSA signature using GrapeSeed's CloudFront private key
//
// ⚠️ GOTCHA: The private key is sensitive. In production, load it from
// AWS Secrets Manager, not from appsettings.json.
//
// 🔗 SEE ALSO: docs/04-aws-services.md#43-cloudfront--global-cdn-and-signed-urls
// =============================================================================

/// <summary>Contract for generating CloudFront signed URLs for video streaming.</summary>
public interface ICloudFrontSignedUrlService
{
    Task<string> GenerateStreamingUrlAsync(
        Guid tenantId,
        Guid videoId,
        TimeSpan validity,
        CancellationToken ct = default);
}

/// <summary>
/// Generates CloudFront signed URLs using the canned policy signer.
/// </summary>
public sealed class CloudFrontSignedUrlService : ICloudFrontSignedUrlService
{
    private readonly string _distributionDomain;
    private readonly string _keyPairId;
    private readonly string _privateKeyPath;
    private readonly ILogger<CloudFrontSignedUrlService> _logger;

    public CloudFrontSignedUrlService(IConfiguration configuration, ILogger<CloudFrontSignedUrlService> logger)
    {
        _distributionDomain = configuration["Aws:CloudFront:DistributionDomain"]
            ?? throw new InvalidOperationException("Aws:CloudFront:DistributionDomain is required.");
        _keyPairId = configuration["Aws:CloudFront:KeyPairId"]
            ?? throw new InvalidOperationException("Aws:CloudFront:KeyPairId is required.");
        _privateKeyPath = configuration["Aws:CloudFront:PrivateKeyPath"]
            ?? throw new InvalidOperationException("Aws:CloudFront:PrivateKeyPath is required.");
        _logger = logger;
    }

    public async Task<string> GenerateStreamingUrlAsync(
        Guid tenantId,
        Guid videoId,
        TimeSpan validity,
        CancellationToken ct = default)
    {
        // 📖 CONCEPT: HLS master playlist URL
        // The HLS player (e.g., hls.js in the browser) starts by loading the
        // master playlist. The playlist lists available quality levels.
        // The player then loads the appropriate segment playlist and streams
        // segments sequentially, switching quality as network conditions change.
        var playlistKey = $"processed/{tenantId}/{videoId}/playlist.m3u8";
        var resourceUrl = $"https://{_distributionDomain}/{playlistKey}";
        var expiresAt = DateTime.UtcNow.Add(validity);

        // 📖 CONCEPT: AmazonCloudFrontUrlSigner
        // This static method from the AWS SDK creates a signed URL using the
        // "Canned Policy" approach. The canned policy restricts access to:
        //   - The specific resource URL
        //   - Until the specified expiry time
        //
        // The "Custom Policy" approach (not used here) allows more flexibility
        // (IP restrictions, date ranges) but produces longer URLs.
        //
        // ⚠️ GOTCHA: The private key file must be in PEM format (RSA, 2048-bit).
        // It is loaded from the filesystem here, but in production it should be
        // loaded from AWS Secrets Manager and cached in memory.
        var signedUrl = AmazonCloudFrontUrlSigner.GetCannedSignedURL(
            resourceUrl: resourceUrl,
            privateKey: await File.ReadAllTextAsync(_privateKeyPath, ct),
            keyPairId: _keyPairId,
            expiresOn: expiresAt
        );

        _logger.LogDebug(
            "Generated CloudFront signed URL for video {VideoId} in tenant {TenantId}, valid until {Expiry}",
            videoId, tenantId, expiresAt);

        return signedUrl;
    }
}
