using System.Net.Http.Headers;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Compression;

/// <summary>
/// The TMS API registers Brotli + Gzip response compression (Program.cs); these tests prove the
/// negotiated <c>Content-Encoding</c> honours the client's <c>Accept-Encoding</c> and stays absent
/// when none is requested. Mirrors the AuthSystem suite, targeting the anonymous JSON health probe.
/// </summary>
[Collection("TranslationApi")]
public sealed class ResponseCompressionTests
{
    private const string CompressibleRoute = "/health/live";

    private readonly TranslationSystemApiFactory _factory;

    public ResponseCompressionTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("br")]
    [InlineData("gzip")]
    public async Task Get_WithAcceptEncoding_ShouldCompressWithRequestedAlgorithm(string encoding)
    {
        // Arrange
        using HttpClient client = _factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Get, CompressibleRoute);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue(encoding));

        // Act
        using HttpResponseMessage response = await client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.ShouldContain(encoding);
    }

    [Fact]
    public async Task Get_WithBrotliPreferredOverGzip_ShouldCompressWithBrotli()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Get, CompressibleRoute);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip", 0.5));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br", 1.0));

        // Act
        using HttpResponseMessage response = await client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.ShouldContain("br");
    }

    [Fact]
    public async Task Get_WithoutAcceptEncoding_ShouldNotCompress()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Get, CompressibleRoute);

        // Act
        using HttpResponseMessage response = await client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.ShouldBeEmpty();
    }
}
