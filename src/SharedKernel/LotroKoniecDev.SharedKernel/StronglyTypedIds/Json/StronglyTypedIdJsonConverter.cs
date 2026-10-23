using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.SharedKernel.StronglyTypedIds.Common;

namespace LotroKoniecDev.SharedKernel.StronglyTypedIds.Json;

/// <summary>
/// Each ID type points at this converter with <c>[JsonConverter(typeof(...))]</c>, so the conversion
/// travels with the ID and no host has to register it.
/// </summary>
public sealed class StronglyTypedIdJsonConverter<TId> : JsonConverter<TId>
    where TId : struct, IStronglyTypedId<TId>
{
    public override TId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        TId.FromValue(reader.GetGuid());

    public override void Write(Utf8JsonWriter writer, TId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}
