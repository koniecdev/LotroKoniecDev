using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Domain.Core.Extensions;

/// <summary>
/// Extension methods for working with Result types.
/// </summary>
public static class ResultExtensions
{
    /// <summary>
    /// Executes an action if the result is successful.
    /// </summary>
    public static Result<T> OnSuccess<T>(this Result<T> result, Action<T> action)
    {
        if (result.IsSuccess)
        {
            action(result.Value);
        }

        return result;
    }

    /// <summary>
    /// Executes an action if the result is a failure.
    /// </summary>
    public static Result<T> OnFailure<T>(this Result<T> result, Action<Error> action)
    {
        if (result.IsFailure)
        {
            action(result.Error);
        }

        return result;
    }

    /// <summary>
    /// Maps the result value using the specified function.
    /// </summary>
    public static Result<TOut> Map<TIn, TOut>(this Result<TIn> result, Func<TIn, TOut> mapper)
    {
        return result.IsSuccess
            ? Result.Success(mapper(result.Value))
            : Result.Failure<TOut>(result.Error);
    }

    /// <summary>
    /// Chains result-returning operations.
    /// </summary>
    public static Result<TOut> Bind<TIn, TOut>(this Result<TIn> result, Func<TIn, Result<TOut>> binder)
    {
        return result.IsSuccess
            ? binder(result.Value)
            : Result.Failure<TOut>(result.Error);
    }

    /// <summary>
    /// Returns the value if successful, otherwise the default value.
    /// </summary>
    public static T GetValueOrDefault<T>(this Result<T> result, T defaultValue = default!)
    {
        return result.IsSuccess ? result.Value : defaultValue;
    }

    /// <summary>
    /// Matches the result to one of two functions based on success/failure.
    /// </summary>
    public static TOut Match<TIn, TOut>(
        this Result<TIn> result,
        Func<TIn, TOut> onSuccess,
        Func<Error, TOut> onFailure)
    {
        return result.IsSuccess
            ? onSuccess(result.Value)
            : onFailure(result.Error);
    }

    /// <summary>
    /// Converts a nullable value to a Result.
    /// </summary>
    public static Result<T> ToResult<T>(this T? value, Error errorIfNull)
        where T : class
    {
        return value is not null
            ? Result.Success(value)
            : Result.Failure<T>(errorIfNull);
    }

    /// <summary>
    /// Combines multiple results into a single result containing all values.
    /// </summary>
    public static Result<IReadOnlyList<T>> Combine<T>(this IEnumerable<Result<T>> results)
    {
        var values = new List<T>();

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
