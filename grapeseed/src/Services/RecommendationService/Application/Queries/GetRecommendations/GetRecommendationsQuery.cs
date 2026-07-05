using GrapeSeed.SharedKernel.Application;
using GrapeSeed.SharedKernel.Application.Behaviors;
using MediatR;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System.Text.Json;

namespace GrapeSeed.RecommendationService.Application.Queries.GetRecommendations;

// =============================================================================
// 📖 CONCEPT: Query — Reading personalised video recommendations
// =============================================================================
// GetRecommendationsQuery demonstrates the full Cache-Aside pattern with Redis:
//
//   1. Check Redis for a cached recommendation list.
//   2. If cache HIT: return immediately (O(1), sub-millisecond).
//   3. If cache MISS:
//       a. Query PostgreSQL for the student's watch history.
//       b. Apply a ranking algorithm (collaborative filtering stub).
//       c. Store the result in Redis with a 5-minute TTL.
//       d. Return the result.
//
// This query does NOT modify state — it only reads. Therefore:
//   - It does NOT implement ITransactionalCommand.
//   - TransactionBehavior skips it.
//   - No database write locks are acquired.
//
// Why 5 minutes TTL?
//   Recommendations don't need to be real-time. A 5-minute lag between
//   a student finishing a video and that video appearing in the "you might
//   also like" section is perfectly acceptable. The time saved on database
//   queries is significant — especially when 1,000 students log in simultaneously.
// =============================================================================

/// <summary>
/// Query to retrieve personalised video recommendations for a student.
/// Results are cached in Redis for 5 minutes.
/// </summary>
public sealed record GetRecommendationsQuery(
    Guid StudentId,
    Guid TenantId,
    int Count = 10
) : IRequest<Result<List<VideoRecommendationDto>>>;

/// <summary>A video recommended to a student, with a confidence score.</summary>
public sealed record VideoRecommendationDto(
    Guid VideoId,
    string Title,
    string ThumbnailUrl,
    TimeSpan Duration,

    /// <summary>
    /// Recommendation confidence score (0.0 to 1.0).
    /// Higher = more likely the student will enjoy this video.
    /// In a real system, this comes from a machine learning model.
    /// Here it's a simplified heuristic based on watch history overlap.
    /// </summary>
    double Score
);

/// <summary>
/// Handles GetRecommendationsQuery: cache-aside pattern with Redis + PostgreSQL fallback.
/// </summary>
public sealed class GetRecommendationsQueryHandler
    : IRequestHandler<GetRecommendationsQuery, Result<List<VideoRecommendationDto>>>
{
    private readonly IDatabase _redis;
    private readonly IWatchHistoryRepository _watchHistoryRepo;
    private readonly IVideoMetadataRepository _videoMetadataRepo;
    private readonly ILogger<GetRecommendationsQueryHandler> _logger;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public GetRecommendationsQueryHandler(
        IConnectionMultiplexer redis,
        IWatchHistoryRepository watchHistoryRepo,
        IVideoMetadataRepository videoMetadataRepo,
        ILogger<GetRecommendationsQueryHandler> logger)
    {
        _redis = redis.GetDatabase();
        _watchHistoryRepo = watchHistoryRepo;
        _videoMetadataRepo = videoMetadataRepo;
        _logger = logger;
    }

    public async Task<Result<List<VideoRecommendationDto>>> Handle(
        GetRecommendationsQuery query,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"recs:{query.TenantId}:{query.StudentId}";

        // ── Step 1: Check Redis cache ──────────────────────────────────────
        var cached = await _redis.StringGetAsync(cacheKey);

        if (cached.HasValue)
        {
            _logger.LogDebug("Cache HIT for recommendations: student {StudentId}", query.StudentId);
            var cachedRecs = JsonSerializer.Deserialize<List<VideoRecommendationDto>>(cached!);
            return Result<List<VideoRecommendationDto>>.Success(
                cachedRecs?.Take(query.Count).ToList() ?? []);
        }

        _logger.LogDebug("Cache MISS for recommendations: student {StudentId}. Computing...", query.StudentId);

        // ── Step 2: Cache miss — query PostgreSQL ──────────────────────────
        var watchedVideoIds = await _watchHistoryRepo.GetWatchedVideoIdsAsync(
            query.StudentId, query.TenantId, cancellationToken);

        // ── Step 3: Collaborative filtering stub ───────────────────────────
        // 📖 CONCEPT: Collaborative Filtering
        // "Students who watched what you watched, also watched these videos."
        //
        // A real implementation would use a machine learning model (e.g., matrix
        // factorisation, neural collaborative filtering). For this learning project,
        // we use a simplified heuristic: find videos with the highest co-watch count
        // with the student's watch history, excluding already-watched videos.
        var candidateVideoIds = await _watchHistoryRepo.GetCoWatchedVideoIdsAsync(
            watchedVideoIds: watchedVideoIds,
            tenantId: query.TenantId,
            excludeVideoIds: watchedVideoIds,
            limit: query.Count * 3, // Over-fetch to allow re-ranking
            cancellationToken: cancellationToken);

        // ── Step 4: Enrich with video metadata ─────────────────────────────
        var recommendations = new List<VideoRecommendationDto>();
        foreach (var (videoId, coWatchScore) in candidateVideoIds)
        {
            var metadata = await _videoMetadataRepo.GetByIdAsync(videoId, query.TenantId, cancellationToken);
            if (metadata is null) continue;

            recommendations.Add(new VideoRecommendationDto(
                VideoId: videoId,
                Title: metadata.Title,
                ThumbnailUrl: metadata.ThumbnailUrl,
                Duration: metadata.Duration,
                Score: coWatchScore
            ));
        }

        // Sort by score descending, take top N
        var topRecommendations = recommendations
            .OrderByDescending(r => r.Score)
            .Take(query.Count)
            .ToList();

        // ── Step 5: Cache the result ───────────────────────────────────────
        // 📖 CONCEPT: Store all recommendations (not just top N) in the cache.
        // This allows serving different page sizes without a cache miss.
        await _redis.StringSetAsync(
            key: cacheKey,
            value: JsonSerializer.Serialize(topRecommendations),
            expiry: CacheTtl
        );

        _logger.LogInformation(
            "Computed {Count} recommendations for student {StudentId}, cached for {Ttl}",
            topRecommendations.Count, query.StudentId, CacheTtl);

        return Result<List<VideoRecommendationDto>>.Success(topRecommendations);
    }
}

// Supporting interfaces (implemented in Infrastructure layer)
public interface IWatchHistoryRepository
{
    Task<List<Guid>> GetWatchedVideoIdsAsync(Guid studentId, Guid tenantId, CancellationToken ct);
    Task<List<(Guid VideoId, double Score)>> GetCoWatchedVideoIdsAsync(
        List<Guid> watchedVideoIds, Guid tenantId, List<Guid> excludeVideoIds, int limit, CancellationToken ct);
}

public interface IVideoMetadataRepository
{
    Task<VideoMetadata?> GetByIdAsync(Guid videoId, Guid tenantId, CancellationToken ct);
}

public sealed record VideoMetadata(Guid Id, string Title, string ThumbnailUrl, TimeSpan Duration);
