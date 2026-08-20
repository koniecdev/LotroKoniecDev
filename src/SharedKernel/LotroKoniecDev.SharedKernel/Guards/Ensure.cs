using System.Runtime.CompilerServices;
using LotroKoniecDev.SharedKernel.StronglyTypedIds.Common;

namespace LotroKoniecDev.SharedKernel.Guards;

public static class Ensure
{
    /// <remarks>
    /// Do not use this with a <see cref="FlagsAttribute"/> enum: a combination of flags is not a
    /// defined value and would fail the check.
    /// </remarks>
    /// <exception cref="ArgumentException">The value is Unset (0).</exception>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined in the enum.</exception>
    public static void IsValidNonDefaultEnum<TEnum>(
        TEnum value,
        [CallerArgumentExpression(nameof(value))] string argumentName = "")
        where TEnum : struct, Enum
    {
        if (EqualityComparer<TEnum>.Default.Equals(value, default))
        {
            throw new ArgumentException($"{argumentName} cannot be default.", argumentName);
        }

        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(argumentName, value,
                $"{argumentName} must be a defined enum value.");
        }
    }

    public static void NotEmpty<T>(
        T id,
        [CallerArgumentExpression(nameof(id))] string argumentName = "")
        where T : IStronglyTypedId<T>
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException($"{argumentName} cannot be empty.", argumentName);
        }
    }

    public static void NotEmpty(
        Guid id,
        [CallerArgumentExpression(nameof(id))] string argumentName = "")
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException($"{argumentName} cannot be empty.", argumentName);
        }
    }

    /// <summary>
    /// Rejects <c>default(DateTimeOffset)</c>, which usually means the caller forgot to set the time.
    /// </summary>
    public static void NotEmpty(
        DateTimeOffset value,
        [CallerArgumentExpression(nameof(value))] string argumentName = "")
    {
        if (value == default)
        {
            throw new ArgumentException($"{argumentName} cannot be default.", argumentName);
        }
    }
}
