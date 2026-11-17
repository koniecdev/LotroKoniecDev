using System.Net;
using System.Text.RegularExpressions;
using LotroKoniecDev.Frontend.Tests.Integration.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.Frontend.Tests.Integration.Tests.Branding;

/// <summary>
/// The home page shows the site's images: the logo, both Discord banners and the tab icon (#1371). A wrong
/// path only shows a broken image, so a test asks for each file the page names.
/// </summary>
public sealed partial class HomePageImageTests : IClassFixture<ProductionFrontendFactory>
{
    /// <summary>Where the page's <c>&lt;base href="/"&gt;</c> resolves a relative path.</summary>
    private static readonly Uri BaseAddress = new("https://localhost/");

    private readonly ProductionFrontendFactory _factory;

    [Fact]
    public async Task Get_HomePage_ServesEveryImageItShows()
    {
        // Arrange
        using WebApplicationFactory<Program> host = CreateHost();
        using CookieJarBrowser browser = new(host);
        using HttpResponseMessage page = await browser.GetAsync("/");
        string markup = await page.Content.ReadAsStringAsync();
        string[] sources = [.. ImageSourceRegex().Matches(markup).Select(match => match.Groups["src"].Value)];

        // Act
        List<string> answers = await RequestEachAsync(browser, sources);

        // Assert
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        // A streamed page can name a file twice, and the asset path carries a fingerprint after the first dot.
        sources.Select(source => source.Split('.')[0]).Distinct()
            .ShouldBe(["logo", "discord-strip", "discord-banner"], ignoreOrder: true);
        answers.ShouldAllBe(answer => answer.EndsWith(" 200 image", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Get_HomePage_ServesItsTabIcon()
    {
        // Arrange
        using WebApplicationFactory<Program> host = CreateHost();
        using CookieJarBrowser browser = new(host);
        using HttpResponseMessage page = await browser.GetAsync("/");
        string markup = await page.Content.ReadAsStringAsync();
        string[] icons = [.. IconLinkRegex().Matches(markup).Select(match => match.Groups["href"].Value)];

        // Act
        List<string> answers = await RequestEachAsync(browser, icons);

        // Assert
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        icons.Length.ShouldBe(1);
        answers.ShouldAllBe(answer => answer.EndsWith(" 200 image", StringComparison.Ordinal));
    }

    public HomePageImageTests(ProductionFrontendFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// The translation API answers 404, so the page shows its calm notice at once and never calls out of
    /// the test.
    /// </summary>
    private WebApplicationFactory<Program> CreateHost() =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient(DashboardTranslationSystem.ClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new NotFoundServer())));

    /// <summary>Each answer as "path: status type", so a failure names the file.</summary>
    private static async Task<List<string>> RequestEachAsync(CookieJarBrowser browser, IEnumerable<string> paths)
    {
        List<string> answers = [];
        foreach (string path in paths)
        {
            using HttpResponseMessage response = await browser.GetAsync(new Uri(BaseAddress, path).PathAndQuery);
            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            answers.Add($"{path}: {(int)response.StatusCode} {mediaType?.Split('/')[0]}");
        }

        return answers;
    }

    /// <summary>An <c>&lt;img src&gt;</c>, or a <c>&lt;source srcset&gt;</c> that holds one file.</summary>
    [GeneratedRegex("""<(?:img\b[^>]*\bsrc|source\b[^>]*\bsrcset)="(?<src>[^"]+)""")]
    private static partial Regex ImageSourceRegex();

    [GeneratedRegex("""<link\b[^>]*\brel="icon"[^>]*\bhref="(?<href>[^"]+)""")]
    private static partial Regex IconLinkRegex();

    private sealed class NotFoundServer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
