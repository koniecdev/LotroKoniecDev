using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.Frontend.Tests.Integration.Shared;

/// <summary>What the stubbed APIs answer and what the stubbed browser posts, shared by the suite's tests.</summary>
public static class StubAnswers
{
    /// <summary>The options the Frontend reads the API answers with (HttpClientApiExtensions).</summary>
    private static readonly JsonSerializerOptions ApiJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>A 200 with a body in the APIs' HATEOAS media type.</summary>
    public static HttpResponseMessage HateoasJson<T>(T body) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(
                body,
                mediaType: new MediaTypeHeaderValue(MediaTypes.HateoasJson),
                options: ApiJsonOptions)
        };

    /// <summary>The import form as a browser sends it: multipart, with the file.</summary>
    public static HttpRequestMessage ImportPost(
        string antiforgeryToken,
        Guid gameVersionId,
        string pageUri = "/import-export")
    {
        ByteArrayContent file = new("1||1||Hello||NULL||NULL||0000000000000000\n"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

        MultipartFormDataContent content = new()
        {
            { new StringContent("import-export"), "_handler" },
            { new StringContent(antiforgeryToken), "__RequestVerificationToken" },
            { new StringContent(gameVersionId.ToString()), "ImportInput.GameVersionId" },
            { file, "ImportInput.File", "exported.txt" }
        };

        return new HttpRequestMessage(HttpMethod.Post, pageUri) { Content = content };
    }
}
