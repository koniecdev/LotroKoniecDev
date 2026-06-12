using System.Text.Json.Serialization.Metadata;
using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.Hateoas.ContentNegotiation;

/// <summary>
/// <see cref="IJsonTypeInfoResolver"/> modifiers that adapt the System.Text.Json
/// contract so HATEOAS hypermedia links disappear entirely from the serialized
/// payload whenever they are empty.
/// <para>
/// Enterprise-grade content negotiation requires that the plain JSON
/// representation contain no trace of HATEOAS. Merely leaving the default
/// <c>Links = []</c> initializer would emit <c>"links": []</c>, which leaks
/// the schema of the HATEOAS representation into the non-HATEOAS one.
/// This modifier applies a <see cref="JsonPropertyInfo.ShouldSerialize"/>
/// predicate to every <see cref="ILinksResponse.Links"/> property so empty
/// collections are omitted from output without forcing every DTO to declare
/// its own attribute.
/// </para>
/// </summary>
public static class HateoasJsonTypeInfoModifiers
{
    /// <summary>
    /// When applied to a <see cref="JsonTypeInfo"/> whose runtime type
    /// implements <see cref="ILinksResponse"/>, configures its
    /// <c>Links</c> property to be serialized only when non-empty.
    /// </summary>
    public static void SuppressEmptyLinks(JsonTypeInfo typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);

        if (!typeof(ILinksResponse).IsAssignableFrom(typeInfo.Type))
        {
            return;
        }

        foreach (JsonPropertyInfo property in typeInfo.Properties)
        {
            if (property.PropertyType != typeof(IReadOnlyCollection<LinkDto>))
            {
                continue;
            }

            property.ShouldSerialize = static (_, value) =>
                value is IReadOnlyCollection<LinkDto> { Count: > 0 };
        }
    }
}
