using LotroKoniecDev.SharedKernel.StronglyTypedIds.Common;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LotroKoniecDev.TranslationSystem.Persistence.Converters;

/// <summary>
/// Reads back through <see cref="IStronglyTypedId{TSelf}.FromValue"/>, which does not validate, because
/// the database is the source of truth. The static helpers exist because an expression tree may not
/// call a static abstract interface member.
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
