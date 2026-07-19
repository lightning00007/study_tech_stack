using BookLibrary.Cqrs.Features.Authors.CreateAuthor;
using BookLibrary.Cqrs.Features.Authors.GetAuthors;
using BookLibrary.Cqrs.Features.Books.CreateBook;
using BookLibrary.Cqrs.Features.Books.GetBook;
using BookLibrary.Cqrs.Features.Books.GetBooks;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace BookLibrary.Cqrs.Api;

// =============================================================================
// API/BOOKSCONTROLLER.CS — Ultra-thin HTTP adapter
// =============================================================================
// Compare this controller with Project 2's BooksController.
// The controller has become EVEN THINNER. It no longer even calls a service —
// it dispatches to MediatR and returns the result.
//
// The controller's only job:
//   1. Receive the HTTP request
//   2. Create a Command or Query object
//   3. Send it to _mediator.Send()
//   4. Map the Result<T> to an HTTP response
//
// That's it. No business logic. No database calls. Just HTTP translation.
//
// BENEFIT: If you later expose this functionality as gRPC, GraphQL, or a
// background job, you create a NEW thin adapter (gRPC controller, GraphQL
// resolver, etc.) that also calls _mediator.Send(). The handler code
// is shared and unchanged.
// =============================================================================

[ApiController]
[Route("api/[controller]")]
public class BooksController : ControllerBase
{
    private readonly IMediator _mediator;

    public BooksController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var result = await _mediator.Send(new GetBooksQuery(), ct);
        return result.IsSuccess ? Ok(result.Value) : StatusCode(500, result.Error);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var result = await _mediator.Send(new GetBookQuery(id), ct);
        return result.IsSuccess ? Ok(result.Value) : NotFound(result.Error);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateBookCommand command, CancellationToken ct)
    {
        var result = await _mediator.Send(command, ct);
        return result.IsSuccess
            ? CreatedAtAction(nameof(GetById), new { id = result.Value }, new { id = result.Value })
            : BadRequest(result.Error);
    }
}

[ApiController]
[Route("api/[controller]")]
public class AuthorsController : ControllerBase
{
    private readonly IMediator _mediator;

    public AuthorsController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var result = await _mediator.Send(new GetAuthorsQuery(), ct);
        return Ok(result.Value);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateAuthorCommand command, CancellationToken ct)
    {
        var result = await _mediator.Send(command, ct);
        return result.IsSuccess
            ? CreatedAtAction(nameof(GetAll), new { id = result.Value }, new { id = result.Value })
            : BadRequest(result.Error);
    }
}
