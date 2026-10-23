using System.Text.Json.Serialization.Metadata;
using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.Hateoas.ContentNegotiation;

/// <summary>
/// A plain JSON response should show no sign of the link representation. The default
/// <c>Links = []</c> initializer would print <c>"links": []</c> and give the shape away, so an empty
/// <see cref="ILinksResponse.Links"/> is left out, and no DTO needs an attribute of its own.
/// </summary>
public static class HateoasJsonTypeInfoModifiers
{
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
