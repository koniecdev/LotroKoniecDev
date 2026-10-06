using System.Net.Http.Headers;
using System.Text;
using LotroKoniecDev.TranslationSystem.API.Auth;
using LotroKoniecDev.TranslationSystem.API.Tests.Unit.Shared;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using NSubstitute;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Auth;

/// <summary>
/// #972: the JWT bearer handler reads the sign-in server's discovery document and signing keys by the
/// charset they name, and that read throws on a name .NET does not know or refuses. The first tests
/// drive the handler alone. The last one goes through the real registration, because a handler that is
/// written but not wired changes nothing.
/// </summary>
public sealed class UnknownCharsetDelegatingHandlerTests
{
    private const string Issuer = "https://auth.lotro.test/";
    private const string Body = """{ "name": "Zażółć gęślą jaźń" }""";

    [Theory]
    [InlineData("utf8")]
    [InlineData("bogus")]
    [InlineData("\"utf8\"")]
    [InlineData("utf-7")]
    public async Task SendAsync_WhenTheAnswerNamesACharsetDotNetCannotUse_LetsItBeReadAsUtf8(string charset)
    {
        HttpMessageInvoker invoker = CreateInvoker(JsonBytes(Encoding.UTF8.GetBytes(Body), charset));

        using HttpRequestMessage request = new(HttpMethod.Get, Issuer);
        using HttpResponseMessage response = await invoker.SendAsync(request, CancellationToken.None);

        (await response.Content.ReadAsStringAsync()).ShouldBe(Body);
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("application/json");
    }

    [Theory]
    [InlineData("utf-8", "utf-8")]
    [InlineData("iso-8859-1", "iso-8859-1")]
    [InlineData("\"iso-8859-1\"", "iso-8859-1")]
    public async Task SendAsync_WhenTheAnswerNamesACharsetDotNetKnows_KeepsIt(string charset, string encodingName)
    {
        // In Latin-1 "é" is the single byte 0xE9, so only a read by the kept charset gives "Café" back.
        HttpMessageInvoker invoker = CreateInvoker(JsonBytes(Encoding.GetEncoding(encodingName).GetBytes("Café"), charset));

        using HttpRequestMessage request = new(HttpMethod.Get, Issuer);
        using HttpResponseMessage response = await invoker.SendAsync(request, CancellationToken.None);

        response.Content.Headers.ContentType.ShouldNotBeNull().CharSet.ShouldBe(charset);
        (await response.Content.ReadAsStringAsync()).ShouldBe("Café");
    }

    [Theory]
    [InlineData("application/json; charset=utf8; charset=bogus")]
    [InlineData("application/json; charset=utf8; charset=utf-7")]
    [InlineData("application/json; charset=utf8; charset=utf-8")]
    public async Task SendAsync_WhenTheRawHeaderNamesTheCharsetTwice_LetsItBeReadAsUtf8(string contentType)
    {
        // The transport keeps the header as raw text and parses it on first use. Once the first unusable
        // name is gone, the second one is what the read sees.
        HttpMessageInvoker invoker = CreateInvoker(() =>
        {
            ByteArrayContent content = new(Encoding.UTF8.GetBytes(Body));
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            return content;
        });

        using HttpRequestMessage request = new(HttpMethod.Get, Issuer);
        using HttpResponseMessage response = await invoker.SendAsync(request, CancellationToken.None);

        (await response.Content.ReadAsStringAsync()).ShouldBe(Body);
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf-7")]
    public async Task JwtBearerMetadata_ThroughTheRealRegistration_ReadsADocumentWithAnUnknownCharset(string charset)
    {
        // The discovery document is the first thing a signed-in call needs. A handler put on the
        // back-channel seam first stands in for the transport.
        StubHttpMessageHandler transport = new(JsonBytes(
            Encoding.UTF8.GetBytes(
                """{ "issuer": "https://auth.lotro.test/", "authorization_endpoint": "https://auth.lotro.test/connect/authorize" }"""),
            charset));
        await using ServiceProvider provider = BuildProvider(transport);
        JwtBearerOptions options = provider
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        OpenIdConnectConfiguration configuration =
            await options.ConfigurationManager!.GetConfigurationAsync(CancellationToken.None);

        configuration.Issuer.ShouldBe(Issuer);
        configuration.AuthorizationEndpoint.ShouldBe("https://auth.lotro.test/connect/authorize");
    }

    private static HttpMessageInvoker CreateInvoker(Func<HttpContent> content) =>
        new(new UnknownCharsetDelegatingHandler { InnerHandler = new StubHttpMessageHandler(content) });

    private static Func<HttpContent> JsonBytes(byte[] body, string charset) =>
        () => new ByteArrayContent(body)
        {
            Headers = { ContentType = new MediaTypeHeaderValue("application/json") { CharSet = charset } }
        };

    private static ServiceProvider BuildProvider(HttpMessageHandler transport)
    {
        IWebHostEnvironment environment = Substitute.For<IWebHostEnvironment>();
        environment.EnvironmentName.Returns("Production");

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:Issuer"] = Issuer,
                ["Auth:Audience"] = "lotrokoniecdev-api"
            })
            .Build());
        services.Configure<JwtBearerOptions>(
            JwtBearerDefaults.AuthenticationScheme,
            options => options.BackchannelHttpHandler = transport);
        services.AddJwtBearerAuthentication(environment);

        return services.BuildServiceProvider();
    }
}
