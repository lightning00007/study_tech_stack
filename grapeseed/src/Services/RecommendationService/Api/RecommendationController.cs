using Microsoft.AspNetCore.Mvc;
using MediatR;
using GrapeSeed.RecommendationService.Application.Queries.GetRecommendations;

namespace GrapeSeed.RecommendationService.Api;

/// <summary>HTTP API for personalised video recommendation retrieval.</summary>
[ApiController]
[Route("api/recommendations")]
public sealed class RecommendationController : ControllerBase
{
    private readonly IMediator _mediator;

    public RecommendationController(IMediator mediator) => _mediator = mediator;

    /// <summary>
    /// Returns a personalised list of recommended videos for the authenticated student.
    /// Results are cached in Redis for 5 minutes.
    /// </summary>
    /// <param name="count">Number of recommendations to return (default: 10, max: 50).</param>
    [HttpGet]
    [ProducesResponseType(typeof(List<VideoRecommendationDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRecommendations(
        [FromQuery] int count = 10,
        CancellationToken cancellationToken = default)
    {
        // Read tenant and student from the headers injected by the API Gateway
        // (which extracted them from the validated JWT)
        if (!Guid.TryParse(Request.Headers["X-Tenant-Id"], out var tenantId) ||
            !Guid.TryParse(Request.Headers["X-Student-Id"], out var studentId))
        {
            return Unauthorized(new ProblemDetails { Detail = "Missing tenant or student identity." });
        }

        var query = new GetRecommendationsQuery(
            StudentId: studentId,
            TenantId: tenantId,
            Count: Math.Min(count, 50) // Cap at 50 to prevent excessive load
        );

        var result = await _mediator.Send(query, cancellationToken);

        if (result.IsFailure)
            return StatusCode(500, new ProblemDetails { Detail = result.Error });

        return Ok(result.Value);
    }
}
