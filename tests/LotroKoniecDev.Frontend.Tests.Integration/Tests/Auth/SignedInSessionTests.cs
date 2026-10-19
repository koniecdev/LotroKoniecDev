using System.Net;
using LotroKoniecDev.Frontend.Tests.Integration.Shared;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.Frontend.Tests.Integration.Tests.Auth;

/// <summary>
/// #1058: the website does not check the access token itself, because the translation API checks every token
/// it receives (ADR-0067 decision 2). Each host here has just started, so it holds no copy of the sign-in
/// server's metadata, and the sign-in server cannot be reached. The real cookie check runs on every request.
/// </summary>
public sealed class SignedInSessionTests : IClassFixture<ProductionFrontendFactory>
{
    private const string SignOutButton = "Wyloguj";

    private readonly ProductionFrontendFactory _factory;

    [Fact]
    public async Task Get_WhileTheSignInServerCannotBeReachedRightAfterARestart_ShowsThePageToTheSignedInUser()
    {
        // Arrange
        using WebApplicationFactory<Program> host = CreateHost(new DashboardTranslationSystem());
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);

        // Act
        using HttpResponseMessage page = await browser.GetAsync("/terms");

        // Assert
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        CookieJarBrowser.SetCookiesOf(page)
            .ShouldNotContain(setCookie => setCookie.StartsWith(SignedInSession.CookieName, StringComparison.Ordinal));
        string markup = await page.Content.ReadAsStringAsync();
        markup.ShouldContain(SignedInSession.UserName);
        markup.ShouldContain(SignOutButton);
    }

    [Fact]
    public async Task Get_TheNextPageAfterTheTranslationApiRefusedTheAccessToken_EndsTheSession()
    {
        // Arrange: the dashboard loads while the API accepts the token, so the service document is cached for
        // this account. Then the signing key changes. The cached document still names the statistics, the API
        // refuses the token there with a 401, and the refused call marks the session dead.
        DashboardTranslationSystem translationSystem = new();
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        await browser.LoadAsync("/dashboard");
        translationSystem.AcceptsTheToken = false;
        await browser.LoadAsync("/dashboard");

        // Act
        using HttpResponseMessage nextPage = await browser.GetAsync("/terms");

        // Assert
        nextPage.StatusCode.ShouldBe(HttpStatusCode.OK);
        CookieJarBrowser.SetCookiesOf(nextPage)
            .ShouldContain(setCookie => setCookie.StartsWith($"{SignedInSession.CookieName}=;", StringComparison.Ordinal));
        string markup = await nextPage.Content.ReadAsStringAsync();
        markup.ShouldNotContain(SignedInSession.UserName);
        markup.ShouldNotContain(SignOutButton);
        translationSystem.Unauthorized.ShouldBe(1);
    }

    [Fact]
    public async Task Get_TheNextPageAfterTheServiceDocumentCameBackWithoutTheSignedInLinks_EndsTheSession()
    {
        // Arrange: nothing is cached yet, and the API refuses the token. Its service document is open to
        // everyone, so it answers with the anonymous links, which marks the session dead.
        using WebApplicationFactory<Program> host = CreateHost(new DashboardTranslationSystem { AcceptsTheToken = false });
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        await browser.LoadAsync("/dashboard");

        // Act
        using HttpResponseMessage nextPage = await browser.GetAsync("/terms");

        // Assert
        nextPage.StatusCode.ShouldBe(HttpStatusCode.OK);
        CookieJarBrowser.SetCookiesOf(nextPage)
            .ShouldContain(setCookie => setCookie.StartsWith($"{SignedInSession.CookieName}=;", StringComparison.Ordinal));
        string markup = await nextPage.Content.ReadAsStringAsync();
        markup.ShouldNotContain(SignedInSession.UserName);
        markup.ShouldNotContain(SignOutButton);
    }

    public SignedInSessionTests(ProductionFrontendFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// <see cref="OpenIdConnectOptions.Configuration"/> stays unset, so the OIDC handler builds its real
    /// configuration manager, which has fetched nothing yet. Every call to the sign-in server fails the way a
    /// call to a server that is down does.
    /// </summary>
    private WebApplicationFactory<Program> CreateHost(DashboardTranslationSystem translationSystem) =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.Configure<OpenIdConnectOptions>(
                OpenIdConnectDefaults.AuthenticationScheme,
                options => options.BackchannelHttpHandler = new UnreachableServer());
            services.AddHttpClient(DashboardTranslationSystem.ClientName)
                .ConfigurePrimaryHttpMessageHandler(() => translationSystem);
        }));
}
