using GrapeSeed.SharedKernel.Application;
using GrapeSeed.VideoService.Application.Commands.UploadVideo;
using GrapeSeed.VideoService.Infrastructure.Cdn;
using MediatR;

namespace GrapeSeed.VideoService.Application.Queries.GetSignedUrl;

/// <summary>Query to obtain a CloudFront signed URL for video streaming.</summary>
public sealed record GetSignedUrlQuery(Guid VideoId, Guid TenantId)
    : IRequest<Result<StreamingUrlDto>>;

/// <summary>The signed CloudFront URL and its expiry.</summary>
public sealed record StreamingUrlDto(
    string StreamingUrl,
    DateTime ExpiresAt,

    /// <summary>The video format. Students with HLS-capable players use the m3u8 URL.</summary>
    string Format = "HLS"
);

/// <summary>
/// Returns a CloudFront signed streaming URL for a ready video.
/// </summary>
public sealed class GetSignedUrlQueryHandler : IRequestHandler<GetSignedUrlQuery, Result<StreamingUrlDto>>
{
    private readonly IVideoRepository _videoRepository;
    private readonly ICloudFrontSignedUrlService _cloudFront;

    public GetSignedUrlQueryHandler(IVideoRepository videoRepository, ICloudFrontSignedUrlService cloudFront)
    {
        _videoRepository = videoRepository;
        _cloudFront = cloudFront;
    }

    public async Task<Result<StreamingUrlDto>> Handle(GetSignedUrlQuery query, CancellationToken ct)
    {
        var video = await _videoRepository.GetByIdAsync(query.VideoId, query.TenantId, ct);

        if (video is null)
            return Result<StreamingUrlDto>.Failure($"Video {query.VideoId} not found.");

        if (video.Status != Domain.VideoStatus.Ready)
            return Result<StreamingUrlDto>.Failure($"Video is not ready for streaming. Current status: {video.Status}");

        // 📖 CONCEPT: 4-hour validity for streaming URLs.
        // Short enough to limit exposure if a URL is leaked.
        // Long enough that a student won't lose access mid-lesson.
        var validity = TimeSpan.FromHours(4);
        var streamUrl = await _cloudFront.GenerateStreamingUrlAsync(query.TenantId, query.VideoId, validity, ct);

        return Result<StreamingUrlDto>.Success(new StreamingUrlDto(
            StreamingUrl: streamUrl,
            ExpiresAt: DateTime.UtcNow.Add(validity)
        ));
    }
}
