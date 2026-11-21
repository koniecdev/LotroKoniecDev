using System.Net;
using LotroKoniecDev.Frontend.Tests.Integration.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.Frontend.Tests.Integration.Tests.CookieConsent;

/// <summary>
/// The page that first shows the cookie notice also sets its cookie, so the notice shows once and closing it
/// needs no request (#1339). Checked on a streamed page and on the 404 page, which the error handling renders
/// again.
/// </summary>
public sealed class CookieConsentNoticeTests : IClassFixture<ProductionFrontendFactory>
{
    private const string CookieName = ".lotrokoniecdev.cookie-consent";
    private const string OpenNotice = """<dialog class="cookie-bar" open""";

    private static readonly TimeSpan StreamTimeout = TimeSpan.FromSeconds(5);

    private readonly ProductionFrontendFactory _factory;

    [Fact]
    public async Task Get_StreamedPage_SetsTheCookieBeforeThePageDataHasLoaded()
    {
        // Arrange
        using HeldServer server = new();
        using WebApplicationFactory<Program> host = CreateHost(() => server);
        using HttpClient client = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost")
        });
        using CancellationTokenSource timeout = new(StreamTimeout);
        string[] setCookies;

        try
        {
            // Act: the headers arrive while the page still waits for the translation API.
            Task<HttpResponseMessage> sending = client.GetAsync("/", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            await server.RequestArrived.Task.WaitAsync(timeout.Token);
            using HttpResponseMessage response = await sending;
            setCookies = CookieJarBrowser.SetCookiesOf(response);
            server.Release();
            await response.Content.ReadAsStringAsync(timeout.Token);
        }
        finally
        {
            server.Release();
        }

        // Assert
        setCookies.ShouldContain(setCookie => setCookie.StartsWith($"{CookieName}=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/no-such-page")]
    public async Task Get_FirstVisit_ShowsTheNotice(string path)
    {
        // Arrange
        using WebApplicationFactory<Program> host = CreateHost(() => new NotFoundServer());
        using CookieJarBrowser browser = new(host);

        // Act
        using HttpResponseMessage response = await browser.GetAsync(path);

        // Assert
        (await response.Content.ReadAsStringAsync()).ShouldContain(OpenNotice);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/no-such-page")]
    public async Task Get_FirstVisit_SetsTheCookie(string path)
    {
        // Arrange
        using WebApplicationFactory<Program> host = CreateHost(() => new NotFoundServer());
        using CookieJarBrowser browser = new(host);

        // Act
        using HttpResponseMessage response = await browser.GetAsync(path);

        // Assert
        CookieJarBrowser.SetCookiesOf(response)
            .ShouldContain(setCookie => setCookie.StartsWith($"{CookieName}=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/no-such-page")]
    public async Task Get_WhenTheNoticeWasShownBefore_ShowsNoNotice(string path)
    {
        // Arrange
        using WebApplicationFactory<Program> host = CreateHost(() => new NotFoundServer());
        using CookieJarBrowser browser = new(host);
        browser.SetCookie(CookieName, "true");

        // Act
        using HttpResponseMessage response = await browser.GetAsync(path);

        // Assert
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("cookie-bar");
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/no-such-page")]
    public async Task Get_WhenTheNoticeWasShownBefore_SetsNoCookie(string path)
    {
        // Arrange
        using WebApplicationFactory<Program> host = CreateHost(() => new NotFoundServer());
        using CookieJarBrowser browser = new(host);
        browser.SetCookie(CookieName, "true");

        // Act
        using HttpResponseMessage response = await browser.GetAsync(path);

        // Assert
        CookieJarBrowser.SetCookiesOf(response)
            .ShouldNotContain(setCookie => setCookie.StartsWith($"{CookieName}=", StringComparison.Ordinal));
    }

    public CookieConsentNoticeTests(ProductionFrontendFactory factory)
    {
        _factory = factory;
    }

    private WebApplicationFactory<Program> CreateHost(Func<HttpMessageHandler> translationSystem) =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient(DashboardTranslationSystem.ClientName)
                .ConfigurePrimaryHttpMessageHandler(translationSystem)));

    /// <summary>
    /// The translation API answers 404, so the home page shows its calm notice at once and never calls out of
    /// the test.
    /// </summary>
    private sealed class NotFoundServer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    /// <summary>Holds its answer until the test releases it, so the page has to stream.</summary>
    private sealed class HeldServer : HttpMessageHandler
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource RequestArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestArrived.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
