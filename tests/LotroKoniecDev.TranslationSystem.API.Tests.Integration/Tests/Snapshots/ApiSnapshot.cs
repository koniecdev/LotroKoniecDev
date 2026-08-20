using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Snapshots;

/// <summary>
/// The shared step from HTTP to snapshot used by the response-contract suites (#571). It sends the request with an
/// explicit <c>Accept</c>, then re-indents the body so the verified file is reviewable line by line
/// instead of one minified blob.
/// </summary>
/// <remarks>
/// Re-indenting passes the payload through <see cref="JsonNode"/>, so what the snapshot pins is the
/// logical contract: property names, nesting, order, JSON types, where a quoted value stays quoted, and
/// the values themselves.
/// It loses exactly one thing: it writes the JSON again with its own relaxed encoder, so how the
/// response encoded non-ASCII is gone. Nothing else in the repo covers that, since
/// <c>ContentNegotiationTests</c> pins the media type and <c>Vary</c> and not the bytes. So
/// <see cref="ShouldServeUnescapedUtf8Async"/> checks it on the raw body, and every snapshot test whose
/// payload contains Polish calls it.
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
    /// Pins the encoding on the wire, which is the one thing <see cref="IndentAsync"/> removes. The
    /// response carries no charset parameter, because JSON is UTF-8 by definition (RFC 8259 §8.1), and
    /// Polish goes out as real UTF-8 and never as <c>\uXXXX</c> escapes.
    /// Switching the serializer to an escaping encoder would change the bytes every client parses while
    /// leaving the snapshot identical, so it needs its own assertion.
    /// </summary>
    public static async Task ShouldServeUnescapedUtf8Async(HttpResponseMessage response, string expectedLiteral)
    {
        response.Content.Headers.ContentType?.CharSet.ShouldBeNull();

        string rawBody = await response.Content.ReadAsStringAsync();
        rawBody.ShouldContain(expectedLiteral);
    }
}
