using LotroKoniecDev.Domain.Core.BuildingBlocks;

namespace LotroKoniecDev.Domain.Core.Monads;

/// <summary>
/// The outcome of an operation: either success, or a failure carrying an <see cref="BuildingBlocks.Error"/>.
/// Business failures are returned this way instead of thrown.
/// </summary>
public class Result
{
    protected Result(bool isSuccess, Error error)
    {
        if (isSuccess && error != Error.None || !isSuccess && error == Error.None)
        {
            throw new InvalidOperationException("Invalid error state for result.");
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    /// <summary><see cref="BuildingBlocks.Error.None"/> when the result is a success.</summary>
    public Error Error { get; }

    public static Result Success() => new(true, Error.None);

    public static Result<TValue> Success<TValue>(TValue value) => new(value, true, Error.None);

    public static Result Failure(Error error) => new(false, error);

    public static Result<TValue> Failure<TValue>(Error error) => new(default!, false, error);
}

/// <summary>
/// A <see cref="Result"/> that also carries a value when it succeeded.
/// </summary>
/// <typeparam name="TValue">The type of the value.</typeparam>
public sealed class Result<TValue> : Result
{
    private readonly TValue _value;

    internal Result(TValue value, bool isSuccess, Error error)
        : base(isSuccess, error)
    {
        _value = value;
    }

    public static implicit operator Result<TValue>(TValue value) => Success(value);

    /// <exception cref="InvalidOperationException">The result is a failure and has no value.</exception>
    public TValue Value => IsSuccess
        ? _value
        : throw new InvalidOperationException("Cannot access the value of a failure result.");
}
