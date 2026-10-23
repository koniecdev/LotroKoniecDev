using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Snapshots;

/// <summary>
/// Re-indents the body so the verified file is reviewable line by line (#571).
/// </summary>
/// <remarks>
/// Going through <see cref="JsonNode"/> pins the logical contract but loses how the response encoded
/// non-ASCII, which nothing else covers. So <see cref="ShouldServeUnescapedUtf8Async"/> checks the raw
/// body, and every snapshot test with Polish in it calls it.
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
    /// No charset parameter (JSON is UTF-8, RFC 8259 §8.1), and Polish as real UTF-8, never
    /// <c>\uXXXX</c>. An escaping encoder would change the bytes but not the snapshot.
    /// </summary>
    public static async Task ShouldServeUnescapedUtf8Async(HttpResponseMessage response, string expectedLiteral)
    {
        response.Content.Headers.ContentType.ShouldNotBeNull().CharSet.ShouldBeNull();

        string rawBody = await response.Content.ReadAsStringAsync();
        rawBody.ShouldContain(expectedLiteral);
    }
}
