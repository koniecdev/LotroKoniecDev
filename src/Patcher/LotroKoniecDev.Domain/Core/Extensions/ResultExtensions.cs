using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Domain.Core.Extensions;

public static class ResultExtensions
{
    public static Result<T> OnSuccess<T>(this Result<T> result, Action<T> action)
    {
        if (result.IsSuccess)
        {
            action(result.Value);
        }

        return result;
    }

    public static Result<T> OnFailure<T>(this Result<T> result, Action<Error> action)
    {
        if (result.IsFailure)
        {
            action(result.Error);
        }

        return result;
    }

    public static Result<TOut> Map<TIn, TOut>(this Result<TIn> result, Func<TIn, TOut> mapper)
    {
        return result.IsSuccess
            ? Result.Success(mapper(result.Value))
            : Result.Failure<TOut>(result.Error);
    }

    /// <summary>
    /// Runs the next step only when this result succeeded, and passes any failure straight through.
    /// </summary>
    public static Result<TOut> Bind<TIn, TOut>(this Result<TIn> result, Func<TIn, Result<TOut>> binder)
    {
        return result.IsSuccess
            ? binder(result.Value)
            : Result.Failure<TOut>(result.Error);
    }

    public static T GetValueOrDefault<T>(this Result<T> result, T defaultValue = default!)
    {
        return result.IsSuccess ? result.Value : defaultValue;
    }

    /// <summary>Runs one function on success and another on failure, and returns what it produced.</summary>
    public static TOut Match<TIn, TOut>(
        this Result<TIn> result,
        Func<TIn, TOut> onSuccess,
        Func<Error, TOut> onFailure)
    {
        return result.IsSuccess
            ? onSuccess(result.Value)
            : onFailure(result.Error);
    }

    /// <summary>Turns a null into a failure carrying <paramref name="errorIfNull"/>.</summary>
    public static Result<T> ToResult<T>(this T? value, Error errorIfNull)
        where T : class
    {
        return value is not null
            ? Result.Success(value)
            : Result.Failure<T>(errorIfNull);
    }

    /// <summary>
    /// Collects the values into one result, or fails with the first error it meets.
    /// </summary>
    public static Result<IReadOnlyList<T>> Combine<T>(this IEnumerable<Result<T>> results)
    {
        List<T> values = new List<T>();

        foreach (Result<T> result in results)
        {
            if (result.IsFailure)
            {
                return Result.Failure<IReadOnlyList<T>>(result.Error);
            }

            values.Add(result.Value);
        }

        return Result.Success<IReadOnlyList<T>>(values);
    }
}
