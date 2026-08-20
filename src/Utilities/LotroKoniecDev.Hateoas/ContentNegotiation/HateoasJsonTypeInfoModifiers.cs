using System.Text.Json.Serialization.Metadata;
using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.Hateoas.ContentNegotiation;

/// <summary>
/// <see cref="IJsonTypeInfoResolver"/> modifiers that drop an empty <c>links</c> array from the
/// serialized payload.
/// <para>
/// A plain JSON response should show no sign of the link representation at all. The default
/// <c>Links = []</c> initializer would print <c>"links": []</c> and give the shape away. This
/// modifier puts a <see cref="JsonPropertyInfo.ShouldSerialize"/> check on every
/// <see cref="ILinksResponse.Links"/> property, so empty collections are left out and no DTO needs
/// an attribute of its own.
/// </para>
/// </summary>
public static class HateoasJsonTypeInfoModifiers
{
    /// <summary>
    /// For a <see cref="JsonTypeInfo"/> whose type implements <see cref="ILinksResponse"/>, makes the
    /// <c>Links</c> property serialize only when it holds something.
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
