namespace BookLibrary.Cqrs.Common;

// =============================================================================
// COMMON/RESULT.CS — Railway-Oriented Programming
// =============================================================================
// In Project 2, we had a simple ServiceResult<T> record. This is the evolved
// version: a proper Result<T> monad inspired by functional programming.
//
// TRADITIONAL ERROR HANDLING (throwing exceptions):
//
//   try
//   {
//       var book = await _service.CreateBookAsync(dto);    // might throw
//       return Created(book);
//   }
//   catch (AuthorNotFoundException ex)
//   {
//       return NotFound(ex.Message);
//   }
//   catch (DuplicateIsbnException ex)
//   {
//       return Conflict(ex.Message);
//   }
//   catch (Exception ex)                                   // catch-all
//   {
//       return StatusCode(500, ex.Message);
//   }
//
// Problems with exceptions for EXPECTED failures:
//   1. Exceptions are INVISIBLE in the method signature — callers don't know what can fail
//   2. Exceptions are SLOW — the CLR must build a full stack trace
//   3. Exceptions break the FLOW of the code — you jump to catch blocks
//   4. Missing a catch block = unhandled exception = 500 error in production
//
// RESULT TYPE APPROACH:
//
//   var result = await _mediator.Send(new CreateBookCommand(...));
//   return result.IsSuccess
//       ? Created(result.Value)
//       : BadRequest(result.Error);
//
// The failure case is visible in the return type. No exception needed.
// No try/catch. The happy path and sad path are side by side.
//
// This is called "Railway-Oriented Programming" (coined by Scott Wlaschin).
// Think of two train tracks: success and failure. Once on the failure track,
// subsequent operations are automatically skipped.
// =============================================================================

/// <summary>
/// Represents the outcome of an operation that returns a value on success.
/// </summary>
public sealed class Result<T>
{
    public T? Value { get; }
    public string? Error { get; }
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;

    private Result(T value)
    {
        Value = value;
        IsSuccess = true;
    }

    private Result(string error)
    {
        Error = error;
        IsSuccess = false;
    }

    /// <summary>Creates a successful result with a value.</summary>
    public static Result<T> Success(T value) => new(value);

    /// <summary>Creates a failed result with an error message.</summary>
    public static Result<T> Failure(string error) => new(error);

    // =========================================================================
    // Monadic operations — allow chaining without if/else
    //
    // Map:  transform the value if success, propagate the error if failure
    // Bind: chain operations that themselves return Result<T>
    // =========================================================================

    /// <summary>
    /// Transforms the success value. If already failed, propagates the failure.
    /// </summary>
    public Result<TOut> Map<TOut>(Func<T, TOut> mapper) =>
        IsFailure ? Result<TOut>.Failure(Error!) : Result<TOut>.Success(mapper(Value!));

    /// <summary>
    /// Chains Result-returning operations. If already failed, skips the binder.
    /// </summary>
    public Result<TOut> Bind<TOut>(Func<T, Result<TOut>> binder) =>
        IsFailure ? Result<TOut>.Failure(Error!) : binder(Value!);

    public override string ToString() =>
        IsSuccess ? $"Success({Value})" : $"Failure({Error})";
}

/// <summary>
/// Non-generic Result for commands that don't return a value.
/// </summary>
public sealed class Result
{
    public string? Error { get; }
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;

    private Result() => IsSuccess = true;
    private Result(string error) { Error = error; IsSuccess = false; }

    public static Result Success() => new();
    public static Result Failure(string error) => new(error);
}
