namespace LotroKoniecDev.SharedKernel.StronglyTypedIds.Common;

/// <summary>
/// Represents a marker interface for strongly-typed IDs.
/// Strongly-typed IDs provide type safety and clarity over the usage of simple Guid values
/// by associating a specific type with the ID.
/// </summary>
/// <typeparam name="TSelf">
/// The strongly-typed ID type that implements this interface.
/// This type should be a struct or a class that encapsulates a Guid value.
/// </typeparam>
public interface IStronglyTypedId<out TSelf> where TSelf : IStronglyTypedId<TSelf>
{
    public Guid Value { get; }

    public static abstract TSelf Create();
    public static abstract TSelf Create(Guid id);
}
