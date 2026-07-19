namespace BookLibrary.CloudNative.Common;

/// <summary>
/// Represents the outcome of an operation that returns a value on success.
/// Uses Railway-Oriented Programming to avoid exceptions for expected failures.
/// </summary>
public sealed class Result<T>
{
    public T? Value { get; }
    public string? Error { get; }
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;

    private Result(T value) { Value = value; IsSuccess = true; }
    private Result(string error) { Error = error; IsSuccess = false; }

    public static Result<T> Success(T value) => new(value);
    public static Result<T> Failure(string error) => new(error);
    public override string ToString() => IsSuccess ? $"Success({Value})" : $"Failure({Error})";
}

/// <summary>Non-generic Result for commands that don't return a value.</summary>
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
