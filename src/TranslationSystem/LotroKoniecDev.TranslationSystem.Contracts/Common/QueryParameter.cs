using System.Text.Json;

namespace LotroKoniecDev.TranslationSystem.Contracts.Common;

/// <summary>
/// The query key is the request property's name in camel case, so the client writes the name the API
/// binds and no option name is written by hand (#1478).
/// </summary>
internal static class QueryParameter
{
    public static KeyValuePair<string, string?> Of(string propertyName, string value)
        => new(JsonNamingPolicy.CamelCase.ConvertName(propertyName), value);
}
