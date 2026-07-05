using GrapeSeed.SharedKernel.Application;
using GrapeSeed.TenantService.Application.Commands.RegisterTenant;
using GrapeSeed.TenantService.Domain;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace GrapeSeed.TenantService.Api;

// =============================================================================
// 📖 CONCEPT: Thin Controller (Anti-Pattern Prevention)
// =============================================================================
// A common mistake in ASP.NET Core is to put business logic in controllers.
// Controllers become bloated, hard to test, and tightly coupled to HTTP.
//
// The GrapeSeed approach: controllers are THIN ADAPTERS.
// Their only responsibilities are:
//   1. Parse and validate the HTTP request shape (which FluentValidation handles).
//   2. Map the request DTO to a MediatR command/query.
//   3. Send the command to MediatR.
//   4. Map the Result back to an appropriate HTTP response.
//
// Nothing else. No database calls. No business logic. No external service calls.
//
// This makes the business logic (in handlers) completely HTTP-independent
// and trivially testable without an HTTP context.
// =============================================================================

/// <summary>HTTP API for tenant registration and management operations.</summary>
[ApiController]
[Route("api/tenants")]
public sealed class TenantController : ControllerBase
{
    private readonly IMediator _mediator;

    public TenantController(IMediator mediator)
    {
        _mediator = mediator;
    }

    // =========================================================================
    // POST /api/tenants/register
    // Public endpoint — no JWT required (this is the onboarding flow)
    // =========================================================================

    /// <summary>
    /// Registers a new school or training centre on the GrapeSeed platform.
    /// Processes payment and provisions the tenant's database schema.
    /// </summary>
    /// <param name="request">Tenant registration details including payment method.</param>
    /// <returns>201 Created with the new tenant ID, or 400/422 on validation/payment failure.</returns>
    [HttpPost("register")]
    [ProducesResponseType(typeof(RegisterTenantResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Register(
        [FromBody] RegisterTenantRequest request,
        CancellationToken cancellationToken)
    {
        // 📖 CONCEPT: DTO → Command mapping
        // The controller's request DTO is separate from the MediatR command.
        // This decoupling lets us rename API fields without changing the command,
        // and vice versa.
        var command = new RegisterTenantCommand(
            Name: request.SchoolName,
            Email: request.ContactEmail,
            PlanId: request.PlanId,
            StripePaymentMethodId: request.StripePaymentMethodId
        );

        var result = await _mediator.Send(command, cancellationToken);

        if (result.IsFailure)
        {
            // 📖 CONCEPT: RFC 7807 Problem Details
            // Problem() returns a standardised error format:
            // { "type": "...", "title": "...", "status": 422, "detail": "Payment failed: ..." }
            return UnprocessableEntity(new ProblemDetails
            {
                Title = "Tenant Registration Failed",
                Detail = result.Error,
                Status = StatusCodes.Status422UnprocessableEntity
            });
        }

        return CreatedAtAction(
            actionName: nameof(GetById),
            routeValues: new { id = result.Value!.Value },
            value: new RegisterTenantResponse(result.Value.Value)
        );
    }

    // =========================================================================
    // GET /api/tenants/{id}
    // =========================================================================

    /// <summary>Retrieves basic information about a tenant by ID.</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        // 🔗 SEE ALSO: GetTenantQuery.cs — the query handler for this endpoint
        var query = new GetTenantQuery(TenantId.From(id));
        var result = await _mediator.Send(query, cancellationToken);

        if (result.IsFailure)
            return NotFound(new ProblemDetails { Detail = result.Error });

        return Ok(result.Value);
    }
}

// =========================================================================
// DTOs (Data Transfer Objects)
// =========================================================================
// DTOs are simple data containers for HTTP requests and responses.
// They have NO business logic and NO domain concepts.
// They exist solely to define the shape of the API contract.

/// <summary>Request body for tenant registration.</summary>
public sealed record RegisterTenantRequest(
    string SchoolName,
    string ContactEmail,
    string PlanId,
    string StripePaymentMethodId
);

/// <summary>Response body returned after successful tenant registration.</summary>
public sealed record RegisterTenantResponse(Guid TenantId);

// Placeholder for query (defined in its own file)
public sealed record GetTenantQuery(TenantId TenantId) : MediatR.IRequest<Result<TenantDto>>;
public sealed record TenantDto(Guid Id, string Name, string Email, string Status, DateTime CreatedAt);
