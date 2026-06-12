using System.Net.Http.Headers;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Compressing;

public sealed class ResponseCompressionTests : EndpointsTestBase
{
    public ResponseCompressionTests(AuthSystemApiFactory appFactory) : base(appFactory)
    {
    }

    [Fact]
    public async Task Response_ShouldBeCompressedWithBrotli_WhenAcceptEncodingBrIsSent()
    {
        // Arrange
        using HttpRequestMessage request = new(HttpMethod.Get, "/health/live");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));

        // Act
        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.ShouldContain("br");
    }

    [Fact]
    public async Task Response_ShouldBeCompressedWithGzip_WhenAcceptEncodingGzipIsSent()
    {
        // Arrange
        using HttpRequestMessage request = new(HttpMethod.Get, "/health/live");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        // Act
        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.ShouldContain("gzip");
    }

    [Fact]
    public async Task Response_ShouldPreferBrotli_WhenBothBrAndGzipAreAccepted()
    {
        // Arrange
        using HttpRequestMessage request = new(HttpMethod.Get, "/health/live");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip", 0.5));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br", 1.0));

        // Act
        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.ShouldContain("br");
    }

    [Fact]
    public async Task Response_ShouldNotBeCompressed_WhenNoAcceptEncodingIsSent()
    {
        // Arrange
        using HttpRequestMessage request = new(HttpMethod.Get, "/health/live");
        request.Headers.AcceptEncoding.Clear();

        // Act
        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.ShouldBeEmpty();
    }
}
