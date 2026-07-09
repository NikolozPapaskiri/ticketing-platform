namespace TicketingPlatform.Application.Common;

public enum ResultError
{
    None,
    NotFound,
    Conflict,
    Unauthorized
}

/// <summary>
/// Outcome of a use case that returns a value. Application services cannot speak HTTP, so
/// expected failures (not found, conflict) come back as an error kind + message; the controller
/// maps the kind to a status code. Exceptions stay reserved for the genuinely unexpected.
/// </summary>
public sealed class Result<T>
{
    public bool IsSuccess { get; }
    public T? Value { get; }
    public ResultError Error { get; }
    public string? Message { get; }

    private Result(bool ok, T? value, ResultError error, string? message)
        => (IsSuccess, Value, Error, Message) = (ok, value, error, message);

    public static Result<T> Success(T value) => new(true, value, ResultError.None, null);
    public static Result<T> NotFound(string message) => new(false, default, ResultError.NotFound, message);
    public static Result<T> Conflict(string message) => new(false, default, ResultError.Conflict, message);

    // Deliberately vague message ("Invalid credentials"), never "wrong password" vs "no such
    // user" - that distinction is an account-enumeration oracle.
    public static Result<T> Unauthorized(string message) => new(false, default, ResultError.Unauthorized, message);
}

/// <summary>Outcome of a use case with no return value (e.g. a state transition).</summary>
public sealed class Result
{
    public bool IsSuccess { get; }
    public ResultError Error { get; }
    public string? Message { get; }

    private Result(bool ok, ResultError error, string? message)
        => (IsSuccess, Error, Message) = (ok, error, message);

    public static Result Success() => new(true, ResultError.None, null);
    public static Result NotFound(string message) => new(false, ResultError.NotFound, message);
    public static Result Conflict(string message) => new(false, ResultError.Conflict, message);
}
