using System.Text.Json.Serialization;
using LotroKoniecDev.SharedKernel.StronglyTypedIds.Common;
using LotroKoniecDev.SharedKernel.StronglyTypedIds.Json;

namespace LotroKoniecDev.SharedKernel.StronglyTypedIds;

[JsonConverter(typeof(StronglyTypedIdJsonConverter<IdentityId>))]
public readonly record struct IdentityId : IStronglyTypedId<IdentityId>
{
    public static readonly IdentityId Empty;

    public Guid Value { get; }

    private IdentityId(Guid value)
    {
        Value = value;
    }

    public static IdentityId Create() => new(Guid.CreateVersion7());

    public static IdentityId Create(Guid id) => new(id);

    public static IdentityId FromValue(Guid id) => new(id);

    public override string ToString() => Value.ToString();
}
