using System.Net.Http.Headers;

namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients;

/// <summary>
/// For an endpoint that answers in headers instead of a body. The values are copied out, so the send
/// helper can dispose the response.
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
