using LotroKoniecDev.SharedKernel.StronglyTypedIds.Common;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LotroKoniecDev.TranslationSystem.Persistence.Converters;

/// <summary>
/// Maps any <see cref="IStronglyTypedId{TSelf}"/> to its underlying Guid column. Rehydrates via the
/// non-validating <see cref="IStronglyTypedId{TSelf}.FromValue"/> — the database is the source of
/// truth. The conversions are routed through static helpers so the expression trees contain no
/// access to a static-abstract interface member (which the compiler forbids in an expression tree).
/// </summary>
public sealed class StronglyTypedIdValueConverter<TId> : ValueConverter<TId, Guid>
    where TId : struct, IStronglyTypedId<TId>
{
    public StronglyTypedIdValueConverter()
        : base(id => ToGuid(id), value => FromGuid(value))
    {
    }

    private static Guid ToGuid(TId id) => id.Value;

    private static TId FromGuid(Guid value) => TId.FromValue(value);
}
