using BookLibrary.CloudNative.Features.Books.CreateBook;
using BookLibrary.CloudNative.Features.Books.GetBooks;
using BookLibrary.CloudNative.Features.Books.PublishBook;
using BookLibrary.CloudNative.Features.Authors.CreateAuthor;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace BookLibrary.CloudNative.Api;

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
        return Ok(result.Value);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateBookCommand command, CancellationToken ct)
    {
        var result = await _mediator.Send(command, ct);
        return result.IsSuccess
            ? CreatedAtAction(nameof(GetAll), new { id = result.Value }, new { id = result.Value })
            : BadRequest(result.Error);
    }

    /// <summary>
    /// Publishes a book, making it publicly visible.
    /// This endpoint demonstrates the full Outbox → SNS flow:
    ///   1. Book.Publish() is called → raises BookPublishedEvent
    ///   2. TransactionBehavior commits: updated book + OutboxMessage in one transaction
    ///   3. OutboxPublisherJob picks up the OutboxMessage and publishes to SNS
    /// </summary>
    [HttpPost("{id:int}/publish")]
    public async Task<IActionResult> Publish(int id, CancellationToken ct)
    {
        var result = await _mediator.Send(new PublishBookCommand(id), ct);
        return result.IsSuccess ? Ok("Book published. Event queued for SNS delivery.") : BadRequest(result.Error);
    }
}

[ApiController]
[Route("api/[controller]")]
public class AuthorsController : ControllerBase
{
    private readonly IMediator _mediator;
    public AuthorsController(IMediator mediator) => _mediator = mediator;

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateAuthorCommand command, CancellationToken ct)
    {
        var result = await _mediator.Send(command, ct);
        return result.IsSuccess ? Created("/api/authors", new { id = result.Value }) : BadRequest(result.Error);
    }
}
