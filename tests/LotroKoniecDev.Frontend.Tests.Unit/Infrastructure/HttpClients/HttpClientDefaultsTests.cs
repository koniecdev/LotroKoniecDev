using System.Net;
using LotroKoniecDev.Frontend.Infrastructure.Auth.TokenRefresh;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.AuthSystemHttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.Frontend.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.HttpClients;

/// <summary>
/// #922: the app's registration switches redirects and cookies off once for every client, so no client
/// has to ask for it. A followed redirect would carry the caller key, the visitor's address and, on a
/// 307 or 308, a form with the refresh token to wherever an answer pointed (#830, #899). One handler
/// serves every visitor, so a kept cookie would go out with other visitors' calls (#924). Every test goes
/// through the real <c>AddFrontend</c>.
/// </summary>
public sealed class HttpClientDefaultsTests
{
    private const string ClientAddedLater = "a-client-added-later";

    [Theory]
    [InlineData(nameof(ITranslationSystemClient))]
    [InlineData(nameof(IAuthSystemClient))]
    [InlineData(nameof(ITokenEndpointClient))]
    [InlineData(ClientAddedLater)]
    public void CreateHandler_ThroughTheRealRegistration_DoesNotFollowRedirects(string clientName)
    {
        using ServiceProvider provider = CreateFrontendServices().BuildServiceProvider();

        List<HttpMessageHandler> chain = HandlerChain(provider, clientName);

        chain[^1].ShouldBeOfType<SocketsHttpHandler>().AllowAutoRedirect.ShouldBeFalse();
    }

    [Theory]
    [InlineData(nameof(ITranslationSystemClient))]
    [InlineData(nameof(IAuthSystemClient))]
    [InlineData(nameof(ITokenEndpointClient))]
    [InlineData(ClientAddedLater)]
    public void CreateHandler_ThroughTheRealRegistration_KeepsNoCookies(string clientName)
    {
        using ServiceProvider provider = CreateFrontendServices().BuildServiceProvider();

        List<HttpMessageHandler> chain = HandlerChain(provider, clientName);

        chain[^1].ShouldBeOfType<SocketsHttpHandler>().UseCookies.ShouldBeFalse();
    }

    /// <summary>
    /// The app-wide transport renews its connections. The TMS and auth typed clients need that most: they
    /// never replace their handler, so without it they would keep their connections forever and never see
    /// a DNS change.
    /// </summary>
    [Theory]
    [InlineData(nameof(ITranslationSystemClient))]
    [InlineData(nameof(IAuthSystemClient))]
    [InlineData(nameof(ITokenEndpointClient))]
    [InlineData(ClientAddedLater)]
    public void CreateHandler_ThroughTheRealRegistration_RenewsItsConnectionsEveryFifteenMinutes(string clientName)
    {
        using ServiceProvider provider = CreateFrontendServices().BuildServiceProvider();

        List<HttpMessageHandler> chain = HandlerChain(provider, clientName);

        chain[^1].ShouldBeOfType<SocketsHttpHandler>().PooledConnectionLifetime.ShouldBe(TimeSpan.FromMinutes(15));
    }

    /// <summary>
    /// A client that needs a transport of its own, for a certificate or a proxy, keeps it, but not the
    /// redirects and cookies a new handler has by default.
    /// </summary>
    [Fact]
    public void CreateHandler_ForAClientGivenItsOwnSocketsHandler_SwitchesOffItsRedirectsAndCookies()
    {
        SocketsHttpHandler transport = new();
        ServiceCollection services = CreateFrontendServices();
        services.AddHttpClient(ClientAddedLater).ConfigurePrimaryHttpMessageHandler(() => transport);
        using ServiceProvider provider = services.BuildServiceProvider();

        List<HttpMessageHandler> chain = HandlerChain(provider, ClientAddedLater);

        chain[^1].ShouldBeSameAs(transport);
        transport.AllowAutoRedirect.ShouldBeFalse();
        transport.UseCookies.ShouldBeFalse();
    }

    [Fact]
    public void CreateHandler_ForAClientGivenItsOwnHttpClientHandler_SwitchesOffItsRedirectsAndCookies()
    {
        HttpClientHandler transport = new();
        ServiceCollection services = CreateFrontendServices();
        services.AddHttpClient<ITokenEndpointClient, TokenEndpointClient>()
            .ConfigurePrimaryHttpMessageHandler(() => transport);
        using ServiceProvider provider = services.BuildServiceProvider();

        List<HttpMessageHandler> chain = HandlerChain(provider, nameof(ITokenEndpointClient));

        chain[^1].ShouldBeSameAs(transport);
        transport.AllowAutoRedirect.ShouldBeFalse();
        transport.UseCookies.ShouldBeFalse();
    }

    /// <summary>The tests' stand-in servers replace the transport the same way, and they must keep working.</summary>
    [Fact]
    public void CreateHandler_ForAClientGivenAStandInServer_KeepsItAsTheTransport()
    {
        StubHttpMessageHandler standIn = StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, "{}");
        ServiceCollection services = CreateFrontendServices();
        services.AddHttpClient<IAuthSystemClient, AuthSystemClient>()
            .ConfigurePrimaryHttpMessageHandler(() => standIn);
        using ServiceProvider provider = services.BuildServiceProvider();

        List<HttpMessageHandler> chain = HandlerChain(provider, nameof(IAuthSystemClient));

        chain[^1].ShouldBeSameAs(standIn);
    }

    private static List<HttpMessageHandler> HandlerChain(ServiceProvider provider, string clientName) =>
        HttpMessageHandlerChain.From(provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(clientName));

    private static ServiceCollection CreateFrontendServices()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IOptions<AuthSystemSettings>>(Microsoft.Extensions.Options.Options.Create(new AuthSystemSettings
        {
            BaseUrl = "https://auth.lotro.test/",
            Authority = "https://auth.lotro.test",
            ClientId = "lotrokoniecdev-web",
            CallbackPath = "/callback",
            SignedOutCallbackPath = "/signout-callback-oidc",
            Scopes = ["openid", "email", "profile"]
        }));
        services.AddSingleton<IOptions<TranslationSystemSettings>>(Microsoft.Extensions.Options.Options.Create(
            new TranslationSystemSettings { BaseUrl = "https://tms.lotro.test/" }));
        services.AddFrontend();

        return services;
    }
}
