namespace LotroKoniecDev.Domain.Core.Monads;

/// <summary>
/// Represents a wrapper around a value that may or may not be present.
/// </summary>
/// <typeparam name="T">The value type (must be a reference type).</typeparam>
public sealed class Maybe<T> : IEquatable<Maybe<T>> where T : class
{
    private readonly T? _value;

    private Maybe(T? value)
    {
        _value = value;
    }

    /// <summary>
    /// Gets a value indicating whether the value exists.
    /// </summary>
    public bool HasValue => !HasNoValue;

    /// <summary>
    /// Gets a value indicating whether the value does not exist.
    /// </summary>
    public bool HasNoValue => _value is null;

    /// <summary>
    /// Gets the value. Throws if no value is present.
    /// </summary>
    public T Value => HasValue
        ? _value!
        : throw new InvalidOperationException("The value cannot be accessed because it does not exist.");

    /// <summary>
    /// Gets the default empty instance.
    /// </summary>
    public static Maybe<T> None => new(null);

    /// <summary>
    /// Creates a new <see cref="Maybe{T}"/> instance from the specified value.
    /// </summary>
    public static Maybe<T> From(T? value) => new(value);

    public static implicit operator Maybe<T>(T value) => From(value);

    public static implicit operator T?(Maybe<T> maybe) => maybe.HasValue ? maybe.Value : null;

    public bool Equals(Maybe<T>? other)
    {
        if (other is null)
        {
            return false;
        }

        if (HasNoValue && other.HasNoValue)
        {
            return true;
        }

        if (HasNoValue || other.HasNoValue)
        {
            return false;
        }

        return Value.Equals(other.Value);
    }

    public override bool Equals(object? obj) =>
        obj switch
        {
            null => false,
            T value => Equals(new Maybe<T>(value)),
            Maybe<T> maybe => Equals(maybe),
            _ => false
        };

    public override int GetHashCode() => HasValue ? Value.GetHashCode() : 0;
}
