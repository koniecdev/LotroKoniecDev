using System.Net.Http.Headers;

namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients;

/// <summary>
/// Response headers kept from a successful API call, for the few endpoints that return their data in
/// headers instead of a body, such as a <c>204 No Content</c> with <c>X-Deletion-Finalizes-At</c> when
/// an account deletion is scheduled. The values are copied out of the
/// <see cref="HttpResponseMessage"/>, so the send helper can dispose it while callers still work with
/// the <c>ApiResult</c> type.
/// </summary>
internal sealed class ApiResponseHeaders
{
    private readonly Dictionary<string, string> _values;

    private ApiResponseHeaders(Dictionary<string, string> values)
    {
        _values = values;
    }

    public static ApiResponseHeaders From(HttpResponseHeaders headers)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, IEnumerable<string>> header in headers)
        {
            string? firstValue = header.Value.FirstOrDefault();
            if (firstValue is not null)
            {
                values[header.Key] = firstValue;
            }
        }

        return new ApiResponseHeaders(values);
    }

    public string? GetValueOrDefault(string name) => _values.GetValueOrDefault(name);
}
