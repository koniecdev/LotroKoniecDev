using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LotroKoniecDev.Frontend.Infrastructure.Auth;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Settings;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.HttpClients;

/// <summary>
/// #972: the OIDC library reads the sign-in server's answers by the charset they name, and that read
/// throws on a name .NET does not know or refuses. The first tests drive the handler alone. The last one
/// goes through the real registration, because a handler that is written but not wired changes nothing.
/// </summary>
public sealed class UnknownCharsetDelegatingHandlerTests
{
    private const string AuthBaseUrl = "https://auth.lotro.test/";

    [Theory]
    [InlineData("utf8")]
    [InlineData("bogus")]
    [InlineData("\"utf8\"")]
    [InlineData("utf-7")]
    public async Task SendAsync_WhenTheAnswerNamesACharsetDotNetCannotUse_LetsItBeReadAsUtf8(string charset)
    {
        const string body = """{ "name": "Zażółć gęślą jaźń" }""";
        HttpMessageInvoker invoker = CreateInvoker(Encoding.UTF8.GetBytes(body), charset);

        using HttpRequestMessage request = new(HttpMethod.Get, AuthBaseUrl);
        using HttpResponseMessage response = await invoker.SendAsync(request, CancellationToken.None);

        (await response.Content.ReadAsStringAsync()).ShouldBe(body);
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("application/json");
    }

    [Theory]
    [InlineData("utf-8", "utf-8")]
    [InlineData("iso-8859-1", "iso-8859-1")]
    [InlineData("\"iso-8859-1\"", "iso-8859-1")]
    public async Task SendAsync_WhenTheAnswerNamesACharsetDotNetKnows_KeepsIt(string charset, string encodingName)
    {
        // In Latin-1 "é" is the single byte 0xE9, so only a read by the kept charset gives "Café" back.
        HttpMessageInvoker invoker = CreateInvoker(Encoding.GetEncoding(encodingName).GetBytes("Café"), charset);

        using HttpRequestMessage request = new(HttpMethod.Get, AuthBaseUrl);
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
        const string body = """{ "name": "Zażółć gęślą jaźń" }""";
        StubHttpMessageHandler inner = StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, () =>
        {
            ByteArrayContent content = new(Encoding.UTF8.GetBytes(body));
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            return content;
        });
        HttpMessageInvoker invoker = new(new UnknownCharsetDelegatingHandler { InnerHandler = inner });

        using HttpRequestMessage request = new(HttpMethod.Get, AuthBaseUrl);
        using HttpResponseMessage response = await invoker.SendAsync(request, CancellationToken.None);

        (await response.Content.ReadAsStringAsync()).ShouldBe(body);
    }

    [Fact]
    public async Task SendAsync_WhenTheAnswerHasNoContentType_ReturnsItAsItCame()
    {
        StubHttpMessageHandler inner = StubHttpMessageHandler.RespondWith(
            HttpStatusCode.OK,
            () => new ByteArrayContent(Encoding.UTF8.GetBytes("Zażółć")));
        HttpMessageInvoker invoker = new(new UnknownCharsetDelegatingHandler { InnerHandler = inner });

        using HttpRequestMessage request = new(HttpMethod.Get, AuthBaseUrl);
        using HttpResponseMessage response = await invoker.SendAsync(request, CancellationToken.None);

        response.Content.Headers.ContentType.ShouldBeNull();
        (await response.Content.ReadAsStringAsync()).ShouldBe("Zażółć");
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf-7")]
    public async Task OpenIdConnectMetadata_ThroughTheRealRegistration_ReadsADocumentWithAnUnknownCharset(string charset)
    {
        // The discovery document is the first thing a sign-in reads, through the back-channel the
        // frontend's registration builds. A handler put on that seam first stands in for the transport.
        StubHttpMessageHandler transport = StubHttpMessageHandler.RespondWith(
            HttpStatusCode.OK,
            JsonBytes(
                Encoding.UTF8.GetBytes(
                    """{ "issuer": "https://auth.lotro.test/", "authorization_endpoint": "https://auth.lotro.test/connect/authorize" }"""),
                charset));
        await using ServiceProvider provider = BuildProvider(transport);
        OpenIdConnectOptions options = provider
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);

        OpenIdConnectConfiguration configuration =
            await options.ConfigurationManager!.GetConfigurationAsync(CancellationToken.None);

        configuration.Issuer.ShouldBe("https://auth.lotro.test/");
        configuration.AuthorizationEndpoint.ShouldBe("https://auth.lotro.test/connect/authorize");
    }

    private static HttpMessageInvoker CreateInvoker(byte[] body, string charset) =>
        new(new UnknownCharsetDelegatingHandler
        {
            InnerHandler = StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, JsonBytes(body, charset))
        });

    private static Func<HttpContent> JsonBytes(byte[] body, string charset) =>
        () => new ByteArrayContent(body)
        {
            Headers = { ContentType = new MediaTypeHeaderValue("application/json") { CharSet = charset } }
        };

    private static ServiceProvider BuildProvider(HttpMessageHandler transport)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton<IOptions<AuthSystemSettings>>(Microsoft.Extensions.Options.Options.Create(new AuthSystemSettings
        {
            BaseUrl = AuthBaseUrl,
            Authority = "https://auth.lotro.test",
            ClientId = "lotrokoniecdev-web",
            CallbackPath = "/callback",
            SignedOutCallbackPath = "/signout-callback-oidc",
            Scopes = ["openid", "email", "profile"]
        }));
        services.Configure<OpenIdConnectOptions>(
            OpenIdConnectDefaults.AuthenticationScheme,
            options => options.BackchannelHttpHandler = transport);
        services.AddFrontendAuthentication();

        return services.BuildServiceProvider();
    }
}
