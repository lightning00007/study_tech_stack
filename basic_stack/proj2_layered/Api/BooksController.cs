using BookLibrary.Layered.Application;
using Microsoft.AspNetCore.Mvc;

namespace BookLibrary.Layered.Api;

// =============================================================================
// API/BOOKSCONTROLLER.CS — The thin HTTP adapter
// =============================================================================
// Compare this controller with Project 1's endpoint lambdas.
// The controller is much simpler now because ALL business logic has moved
// to BookService. The controller only does three things:
//
//   1. Parse the HTTP request (model binding does this automatically)
//   2. Call the service
//   3. Map the service result to an HTTP response
//
// The controller doesn't know anything about the database.
// It doesn't know what SQL runs. It doesn't know if there's even a database —
// the service could read from a file, an in-memory cache, or an API.
// This is the Single Responsibility Principle applied to HTTP controllers.
// =============================================================================

[ApiController]
[Route("api/[controller]")]
public class BooksController : ControllerBase
{
    private readonly IBookService _bookService;

    // IBookService is injected — not created here.
    public BooksController(IBookService bookService)
    {
        _bookService = bookService;
    }

    /// <summary>Get all books in the library.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<BookDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var books = await _bookService.GetAllBooksAsync(ct);
        return Ok(books);
    }

    /// <summary>Get a single book by its ID.</summary>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(BookDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var result = await _bookService.GetBookByIdAsync(id, ct);

        return result.IsSuccess
            ? Ok(result.Value)
            : NotFound(result.Error);
    }

    /// <summary>Create a new book linked to an existing author.</summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CreateBookDto dto, CancellationToken ct)
    {
        var result = await _bookService.CreateBookAsync(dto, ct);

        return result.IsSuccess
            ? CreatedAtAction(nameof(GetById), new { id = result.Value }, new { id = result.Value })
            : BadRequest(result.Error);
    }
}

[ApiController]
[Route("api/[controller]")]
public class AuthorsController : ControllerBase
{
    private readonly IAuthorService _authorService;

    public AuthorsController(IAuthorService authorService)
    {
        _authorService = authorService;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct) =>
        Ok(await _authorService.GetAllAuthorsAsync(ct));

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var result = await _authorService.GetAuthorByIdAsync(id, ct);
        return result.IsSuccess ? Ok(result.Value) : NotFound(result.Error);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateAuthorDto dto, CancellationToken ct)
    {
        var result = await _authorService.CreateAuthorAsync(dto, ct);
        return result.IsSuccess
            ? CreatedAtAction(nameof(GetById), new { id = result.Value }, new { id = result.Value })
            : BadRequest(result.Error);
    }
}
