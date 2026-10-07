using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry.Instrumentation.Http;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Telemetry;

/// <summary>
/// Reads the trace filter the real host composed in <c>Program.cs</c>. A breach check's URL ends in the
/// password's hash prefix, so it must never become a span next to the request and its user (ADR-0065).
/// </summary>
[Collection("AuthApi")]
public sealed class HttpClientTracingTests
{
    private readonly AuthSystemApiFactory _factory;

    public HttpClientTracingTests(AuthSystemApiFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("https://api.pwnedpasswords.com/range/5BAA6", false)]
    [InlineData("https://auth.lotro-translator.pl/connect/token", true)]
    [InlineData("https://smtp-relay.brevo.com/", true)]
    public void HttpClientTracing_ForEachOutgoingRequest_TracesEverythingButTheBreachCheck(
        string requestUri,
        bool expectedTraced)
    {
        // Arrange
        HttpClientTraceInstrumentationOptions options = _factory.Services
            .GetRequiredService<IOptionsMonitor<HttpClientTraceInstrumentationOptions>>()
            .Get(Microsoft.Extensions.Options.Options.DefaultName);
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(requestUri));

        // Act
        bool traced = options.FilterHttpRequestMessage?.Invoke(request) ?? true;

        // Assert
        traced.ShouldBe(expectedTraced);
    }
}
