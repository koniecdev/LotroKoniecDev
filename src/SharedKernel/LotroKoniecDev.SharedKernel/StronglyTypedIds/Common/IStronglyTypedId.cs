namespace LotroKoniecDev.SharedKernel.StronglyTypedIds.Common;

/// <summary>
/// Marker interface for strongly-typed IDs. Wrapping a Guid in its own type means the compiler
/// catches an id passed where a different id was meant.
/// </summary>
/// <typeparam name="TSelf">
/// The strongly-typed ID type implementing this interface. It wraps a single Guid value.
/// </typeparam>
public interface IStronglyTypedId<out TSelf> where TSelf : IStronglyTypedId<TSelf>
{
    public Guid Value { get; }

    public static abstract TSelf Create();
    public static abstract TSelf Create(Guid id);

    /// <summary>
    /// Rebuilds an ID from a trusted source, such as EF materialization or JSON deserialization,
    /// without running domain validation: the store is the source of truth. Input we do not trust
    /// must go through <see cref="Create(Guid)"/> instead.
    /// </summary>
    public static abstract TSelf FromValue(Guid id);
}
