using System.Net.Http.Headers;

namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients;

/// <summary>
/// Response headers captured from a successful API call, for the rare endpoints that return their
/// payload via headers instead of a body (e.g. <c>204 No Content</c> + <c>X-Deletion-Finalizes-At</c>
/// on account-deletion scheduling). The values are detached from the <see cref="HttpResponseMessage"/>
/// so the message can stay disposed inside the send helper while callers keep the <c>ApiResult</c> monad.
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
