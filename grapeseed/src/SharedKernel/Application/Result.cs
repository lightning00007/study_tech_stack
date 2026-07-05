namespace GrapeSeed.SharedKernel.Application;

// =============================================================================
// 📖 CONCEPT: Result<T> — Railway-Oriented Programming
// =============================================================================
// In traditional C# code, errors are communicated via exceptions. Exceptions
// are a poor fit for *expected* failure scenarios (invalid input, business rule
// violations) because:
//
//   1. They are slow — the CLR must build a stack trace.
//   2. They are invisible — the method signature says nothing about possible failures.
//   3. They force callers to use try/catch, cluttering business logic.
//
// The Result<T> type is a better alternative: a method either succeeds and
// returns a value, or fails and returns an error description. The caller is
// forced to handle both cases.
//
// This is called "Railway-Oriented Programming" — think of two train tracks:
// one for success, one for failure. Operations chain together, and if any
// operation puts the train on the failure track, subsequent operations are
// skipped automatically.
//
// 🔗 INSPIRED BY: Scott Wlaschin's "Railway Oriented Programming" (F#)
//                 Scott Millett's "Patterns, Principles, and Practices of DDD"
// =============================================================================

/// <summary>
/// Represents the outcome of an operation that can either succeed (with a value) or fail (with an error).
/// </summary>
/// <typeparam name="T">The type of the value returned on success.</typeparam>
public sealed class Result<T>
{
    public T? Value { get; }
    public string? Error { get; }

    // 💡 WHY: IsSuccess drives the caller to explicitly handle both cases
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

    /// <summary>Creates a successful result carrying a value.</summary>
    public static Result<T> Success(T value) => new(value);

    /// <summary>Creates a failed result carrying an error description.</summary>
    public static Result<T> Failure(string error) => new(error);

    // =========================================================================
    // 📖 CONCEPT: Monadic chaining with Map and Bind
    // These methods allow composing operations in a pipeline without
    // explicit if/else null checks at every step.
    //
    // Map:  transforms the success value (success → success or failure → failure)
    // Bind: chains operations that themselves return Result<T>
    // =========================================================================

    /// <summary>
    /// Transforms the success value using a mapping function.
    /// If this result is already a failure, the function is not called.
    /// </summary>
    public Result<TOut> Map<TOut>(Func<T, TOut> mapper)
    {
        if (IsFailure) return Result<TOut>.Failure(Error!);
        return Result<TOut>.Success(mapper(Value!));
    }

    /// <summary>
    /// Chains operations that return Result.
    /// If this result is already a failure, the binder is not called.
    /// </summary>
    public Result<TOut> Bind<TOut>(Func<T, Result<TOut>> binder)
    {
        if (IsFailure) return Result<TOut>.Failure(Error!);
        return binder(Value!);
    }

    public override string ToString() =>
        IsSuccess ? $"Success({Value})" : $"Failure({Error})";
}

/// <summary>
/// Non-generic Result for operations that don't return a value (commands).
/// </summary>
public sealed class Result
{
    public string? Error { get; }
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;

    private Result() => IsSuccess = true;
    private Result(string error)
    {
        Error = error;
        IsSuccess = false;
    }

    public static Result Success() => new();
    public static Result Failure(string error) => new(error);
}
