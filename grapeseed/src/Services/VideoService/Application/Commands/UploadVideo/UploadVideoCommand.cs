using GrapeSeed.SharedKernel.Application;
using GrapeSeed.SharedKernel.Application.Behaviors;
using GrapeSeed.VideoService.Infrastructure.Storage;
using MediatR;

namespace GrapeSeed.VideoService.Application.Commands.UploadVideo;

/// <summary>Initiates a video upload by creating a Video record and returning a pre-signed S3 URL.</summary>
public sealed record UploadVideoCommand(
    Guid TenantId,
    string Title,
    string? Description,
    string FileExtension
) : IRequest<Result<UploadVideoResult>>, ITransactionalCommand;

/// <summary>The result contains the new Video ID and the pre-signed URL for direct S3 upload.</summary>
public sealed record UploadVideoResult(
    Guid VideoId,

    /// <summary>
    /// The pre-signed S3 URL. The client should HTTP PUT the binary video file to this URL.
    /// The URL is valid for 15 minutes. After expiry, the client must request a new URL.
    /// </summary>
    string UploadUrl,

    DateTime UrlExpiresAt
);

/// <summary>
/// Creates a Video record in "Uploading" status and generates a pre-signed S3 upload URL.
/// </summary>
public sealed class UploadVideoCommandHandler : IRequestHandler<UploadVideoCommand, Result<UploadVideoResult>>
{
    private readonly IS3StorageService _s3;
    private readonly IVideoRepository _videoRepository;

    public UploadVideoCommandHandler(IS3StorageService s3, IVideoRepository videoRepository)
    {
        _s3 = s3;
        _videoRepository = videoRepository;
    }

    public async Task<Result<UploadVideoResult>> Handle(UploadVideoCommand command, CancellationToken ct)
    {
        var videoId = Guid.NewGuid();
        var s3Key = S3StorageService.BuildRawVideoKey(command.TenantId, videoId, command.FileExtension);

        // 📖 CONCEPT: Create the Video record FIRST (in status Uploading).
        // This allows the VideoService to track the upload intent.
        // If the upload never completes, a cleanup job can delete orphaned records.
        await _videoRepository.AddAsync(new Domain.Video(
            videoId, command.TenantId, command.Title, command.Description, s3Key), ct);

        // Generate the pre-signed S3 PUT URL (valid for 15 minutes)
        var uploadUrl = await _s3.GenerateUploadUrlAsync(s3Key, TimeSpan.FromMinutes(15), ct);
        var expiresAt = DateTime.UtcNow.AddMinutes(15);

        return Result<UploadVideoResult>.Success(new UploadVideoResult(videoId, uploadUrl, expiresAt));
    }
}

public interface IVideoRepository
{
    Task AddAsync(Domain.Video video, CancellationToken ct = default);
    Task<Domain.Video?> GetByIdAsync(Guid videoId, Guid tenantId, CancellationToken ct = default);
    Task UpdateAsync(Domain.Video video, CancellationToken ct = default);
}
