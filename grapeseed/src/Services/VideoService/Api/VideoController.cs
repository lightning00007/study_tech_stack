using Microsoft.AspNetCore.Mvc;
using MediatR;
using GrapeSeed.VideoService.Application.Commands.UploadVideo;
using GrapeSeed.VideoService.Application.Queries.GetSignedUrl;

namespace GrapeSeed.VideoService.Api;

/// <summary>HTTP API for video upload, management, and streaming URL generation.</summary>
[ApiController]
[Route("api/videos")]
public sealed class VideoController : ControllerBase
{
    private readonly IMediator _mediator;

    public VideoController(IMediator mediator) => _mediator = mediator;

    // =========================================================================
    // POST /api/videos/upload-url
    // Teacher requests a pre-signed URL to upload directly to S3.
    // =========================================================================

    /// <summary>
    /// Generates a pre-signed S3 URL for direct video upload.
    /// The teacher uses this URL to PUT the video file directly to S3.
    /// </summary>
    [HttpPost("upload-url")]
    public async Task<IActionResult> GetUploadUrl(
        [FromBody] GetUploadUrlRequest request,
        CancellationToken cancellationToken)
    {
        var command = new UploadVideoCommand(
            TenantId: request.TenantId,
            Title: request.Title,
            Description: request.Description,
            FileExtension: request.FileExtension
        );

        var result = await _mediator.Send(command, cancellationToken);

        if (result.IsFailure)
            return BadRequest(new ProblemDetails { Detail = result.Error });

        return Ok(result.Value);
    }

    // =========================================================================
    // GET /api/videos/{id}/stream-url
    // Student requests a signed CloudFront URL to stream the video.
    // =========================================================================

    /// <summary>
    /// Returns a time-limited CloudFront signed URL for streaming a video.
    /// The student's HLS player uses this URL to stream video segments.
    /// </summary>
    [HttpGet("{id:guid}/stream-url")]
    public async Task<IActionResult> GetStreamUrl(
        Guid id,
        [FromQuery] Guid tenantId,
        CancellationToken cancellationToken)
    {
        var query = new GetSignedUrlQuery(VideoId: id, TenantId: tenantId);
        var result = await _mediator.Send(query, cancellationToken);

        if (result.IsFailure)
            return result.Error!.Contains("not found")
                ? NotFound(new ProblemDetails { Detail = result.Error })
                : BadRequest(new ProblemDetails { Detail = result.Error });

        return Ok(result.Value);
    }
}

public sealed record GetUploadUrlRequest(
    Guid TenantId,
    string Title,
    string? Description,
    string FileExtension
);
