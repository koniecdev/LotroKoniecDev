using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Snapshots;

/// <summary>
/// The HTTP-to-snapshot seam shared by the response-contract suites (#571): issues the request with an
/// explicit <c>Accept</c>, then re-indents the body so the verified file is reviewable line by line
/// instead of one minified blob.
/// </summary>
/// <remarks>
/// Re-indenting round-trips the payload through <see cref="JsonNode"/>, so what the snapshot pins is
/// the <em>logical</em> contract — property names, nesting, ordering, JSON types (a quoted value stays
/// quoted) and values. The round-trip is lossy in exactly one direction: it re-encodes with a relaxed
/// encoder of its own, erasing how the transport encoded non-ASCII. Nothing else in the repo covers
/// that (<c>ContentNegotiationTests</c> pins the media type and <c>Vary</c>, not the bytes), so
/// <see cref="ShouldServeUnescapedUtf8Async"/> asserts it on the raw body and every snapshot test
/// whose payload carries Polish calls it.
/// </remarks>
internal static class ApiSnapshot
{
    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static Task<HttpResponseMessage> GetHateoasAsync(HttpClient client, string url) =>
        GetAsync(client, url, MediaTypes.HateoasJson);

    public static async Task<HttpResponseMessage> GetAsync(HttpClient client, string url, string accept)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));

        return await client.SendAsync(request);
    }

    public static async Task<string> IndentAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        JsonNode node = JsonNode.Parse(body)
                        ?? throw new InvalidOperationException($"Response body was not JSON: '{body}'.");
        return node.ToJsonString(IndentedJson);
    }

    /// <summary>
    /// Pins the wire encoding, which is the one thing <see cref="IndentAsync"/> normalizes away: the
    /// response carries <em>no</em> charset parameter — JSON is UTF-8 by definition (RFC 8259 §8.1) —
    /// and Polish goes out as literal UTF-8, never as <c>\uXXXX</c> escape sequences. Switching the
    /// serializer to an escaping encoder would change the bytes every client parses while leaving the
    /// snapshot byte-identical, so it needs an assertion of its own.
    /// </summary>
    public static async Task ShouldServeUnescapedUtf8Async(HttpResponseMessage response, string expectedLiteral)
    {
        response.Content.Headers.ContentType?.CharSet.ShouldBeNull();

        string rawBody = await response.Content.ReadAsStringAsync();
        rawBody.ShouldContain(expectedLiteral);
    }
}
