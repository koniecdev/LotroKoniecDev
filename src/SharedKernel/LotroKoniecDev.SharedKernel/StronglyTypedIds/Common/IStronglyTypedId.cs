namespace LotroKoniecDev.SharedKernel.StronglyTypedIds.Common;

public interface IStronglyTypedId<out TSelf> where TSelf : IStronglyTypedId<TSelf>
{
    public Guid Value { get; }

    public static abstract TSelf Create();
    public static abstract TSelf Create(Guid id);

    /// <summary>
    /// Rebuilds an ID from a trusted source, such as EF materialization or JSON deserialization,
    /// without domain validation. Input we do not trust goes through <see cref="Create(Guid)"/>.
    /// </summary>
    public static abstract TSelf FromValue(Guid id);
}
