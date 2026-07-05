using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;
using Microsoft.Extensions.Logging;

namespace GrapeSeed.SharedKernel.Infrastructure.Observability;

// =============================================================================
// 📖 CONCEPT: Custom CloudWatch Metrics
// =============================================================================
// Beyond the standard infrastructure metrics (CPU, memory, network) that AWS
// provides automatically, GrapeSeed publishes BUSINESS metrics to CloudWatch.
//
// Why business metrics?
//   Infrastructure metrics tell you "the server is slow".
//   Business metrics tell you "students are not watching videos" — which may
//   indicate a slow server, a content quality problem, or a UI bug.
//   Both are needed for a complete picture.
//
// GrapeSeed metrics:
//   - GrapeSeed/VideoWatchDuration: how long students watch (engagement)
//   - GrapeSeed/RecommendationCacheHitRate: Redis efficiency
//   - GrapeSeed/TenantRegistrationDuration: onboarding funnel speed
//   - GrapeSeed/TranscodingQueueDepth: number of videos awaiting processing
//
// 📖 CONCEPT: Dimensions
// Dimensions allow you to slice metrics by tenant, service, or environment.
// Example: "average VideoWatchDuration for TenantId=school-a in the last 24h"
// Without dimensions, you'd only see the platform-wide average.
//
// 🔗 SEE ALSO: docs/04-aws-services.md#46-cloudwatch--observability
// =============================================================================

/// <summary>
/// Publishes custom business metrics to AWS CloudWatch.
/// </summary>
public sealed class CloudWatchMetrics
{
    private const string Namespace = "GrapeSeed";
    private readonly IAmazonCloudWatch _cloudWatch;
    private readonly ILogger<CloudWatchMetrics> _logger;

    public CloudWatchMetrics(IAmazonCloudWatch cloudWatch, ILogger<CloudWatchMetrics> logger)
    {
        _cloudWatch = cloudWatch;
        _logger = logger;
    }

    /// <summary>
    /// Records the duration a student watched a video.
    /// Used to measure content engagement.
    /// </summary>
    public async Task RecordVideoWatchAsync(
        Guid tenantId, Guid videoId, TimeSpan watchDuration, CancellationToken ct = default)
    {
        await PublishMetricAsync(
            metricName: "VideoWatchDuration",
            value: watchDuration.TotalSeconds,
            unit: StandardUnit.Seconds,
            dimensions: new()
            {
                // 📖 CONCEPT: Dimensions enable per-tenant analysis.
                // You can query: "average watch duration for school-a vs school-b"
                ["TenantId"] = tenantId.ToString(),
                ["VideoId"] = videoId.ToString()
            },
            ct);
    }

    /// <summary>
    /// Records whether a recommendation request was served from cache or computed.
    /// Used to track Redis efficiency.
    /// </summary>
    public async Task RecordRecommendationCacheResultAsync(
        bool cacheHit, Guid tenantId, CancellationToken ct = default)
    {
        await PublishMetricAsync(
            metricName: "RecommendationCacheHits",
            value: cacheHit ? 1 : 0,
            unit: StandardUnit.Count,
            dimensions: new() { ["TenantId"] = tenantId.ToString() },
            ct);
    }

    /// <summary>
    /// Records how long the tenant registration + payment flow took end-to-end.
    /// Used to monitor and optimise the onboarding funnel.
    /// </summary>
    public async Task RecordTenantRegistrationDurationAsync(
        TimeSpan duration, bool success, CancellationToken ct = default)
    {
        await PublishMetricAsync(
            metricName: "TenantRegistrationDuration",
            value: duration.TotalMilliseconds,
            unit: StandardUnit.Milliseconds,
            dimensions: new() { ["Outcome"] = success ? "Success" : "Failure" },
            ct);
    }

    // =========================================================================
    // Private helper
    // =========================================================================

    private async Task PublishMetricAsync(
        string metricName,
        double value,
        StandardUnit unit,
        Dictionary<string, string> dimensions,
        CancellationToken ct)
    {
        try
        {
            var request = new PutMetricDataRequest
            {
                Namespace = Namespace,
                MetricData =
                [
                    new MetricDatum
                    {
                        MetricName = metricName,
                        Value = value,
                        Unit = unit,
                        Timestamp = DateTime.UtcNow,
                        Dimensions = dimensions
                            .Select(kv => new Dimension { Name = kv.Key, Value = kv.Value })
                            .ToList()
                    }
                ]
            };

            await _cloudWatch.PutMetricDataAsync(request, ct);
        }
        catch (Exception ex)
        {
            // ⚠️ GOTCHA: Never let metric publishing failure affect the main flow.
            // Observability is important but it's a secondary concern. If CloudWatch
            // is temporarily unavailable, the business operation should still succeed.
            _logger.LogWarning(ex, "Failed to publish CloudWatch metric {MetricName}. Continuing.", metricName);
        }
    }
}
