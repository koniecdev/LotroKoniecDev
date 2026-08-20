namespace LotroKoniecDev.Domain.Core.Monads;

/// <summary>
/// Holds a value that may or may not be there, so the absent case cannot be forgotten.
/// </summary>
/// <typeparam name="T">The value type. It must be a reference type.</typeparam>
public sealed class Maybe<T> : IEquatable<Maybe<T>> where T : class
{
    private readonly T? _value;

    private Maybe(T? value)
    {
        _value = value;
    }

    public bool HasValue => !HasNoValue;

    public bool HasNoValue => _value is null;

    /// <summary>Throws when there is no value, so check <see cref="HasValue"/> first.</summary>
    public T Value => HasValue
        ? _value!
        : throw new InvalidOperationException("The value cannot be accessed because it does not exist.");

    public static Maybe<T> None => new(null);

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
