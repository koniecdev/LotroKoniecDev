using Microsoft.AspNetCore.Mvc;

namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients;

/// <summary>
/// A small result type for HTTP calls. On failure it carries the API's <see cref="ProblemDetails"/>, or
/// one we built for a transport failure, so a page can show a Polish message instead of throwing. It is
/// the same idea as the <c>Result</c> type the APIs use, applied to the Frontend's HTTP calls.
/// </summary>
public class ApiResult
{
    protected ApiResult(bool isSuccess, ProblemDetails? problemDetails)
    {
        if (isSuccess && problemDetails is not null)
        {
            throw new InvalidOperationException();
        }

        IsSuccess = isSuccess;
        ProblemDetails = problemDetails;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public ProblemDetails? ProblemDetails { get; }

    public static ApiResult Success() => new(true, null);

    public static ApiResult<TValue> Success<TValue>(TValue value) => new(value, true, null);

    public static ApiResult Failure(ProblemDetails problemDetails) => new(false, problemDetails);

    public static ApiResult<TValue> Failure<TValue>(ProblemDetails problemDetails) =>
        new(default!, false, problemDetails);
}

public sealed class ApiResult<TValue> : ApiResult
{
    internal ApiResult(TValue value, bool isSuccess, ProblemDetails? problemDetails)
        : base(isSuccess, problemDetails)
    {
        Value = value;
    }

    public static implicit operator ApiResult<TValue>(TValue value) => Success(value);

    public TValue Value => IsSuccess
        ? field
        : throw new InvalidOperationException("The value of a failure result can not be accessed.");
}
