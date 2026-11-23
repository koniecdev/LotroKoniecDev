using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using LotroKoniecDev.Frontend.Tests.Integration.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.Frontend.Tests.Integration.Tests.Branding;

/// <summary>
/// The page head that search engines and link previews read (#1416): the home page title, one canonical
/// address per public page, and the preview tags on every page. Only the head counts, because a preview
/// bot never reads the streamed update at the end of the body.
/// </summary>
public sealed partial class LinkPreviewTagsTests : IClassFixture<ProductionFrontendFactory>
{
    private const string HomeTitle = "Spolszczenie LOTRO – The Lord of the Rings Online po polsku";

    private const string OtherHost = "staging.lotro.invalid";

    private static readonly Uri BaseAddress = new("https://localhost");

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly (string Attribute, string Name)[] SiteWideKeys =
    [
        ("property", "og:site_name"),
        ("property", "og:locale"),
        ("property", "og:type"),
        ("property", "og:image:width"),
        ("property", "og:image:height"),
        ("property", "og:image:alt"),
        ("name", "twitter:card")
    ];

    private readonly ProductionFrontendFactory _factory;

    [Fact]
    public async Task Get_HomePage_HasTheSpolszczenieTitle()
    {
        // Arrange
        using WebApplicationFactory<Program> host = CreateHost(new NotFoundServer());
        using CookieJarBrowser browser = new(host);

        // Act
        string head = await ReadHeadAsync(browser, "/");

        // Assert
        TitlesOf(head).ShouldBe([HomeTitle]);
    }

    [Theory]
    [InlineData("/", "https://localhost/")]
    [InlineData("/?fbclid=abc", "https://localhost/")]
    [InlineData("/regulamin", "https://localhost/regulamin")]
    [InlineData("/terms", "https://localhost/regulamin")]
    [InlineData("/regulamin?utm_source=discord", "https://localhost/regulamin")]
    public async Task Get_PublicPage_GivesItsOneAddressAsCanonicalAndOgUrl(string path, string expectedAddress)
    {
        // Arrange
        using WebApplicationFactory<Program> host = CreateHost(new NotFoundServer());
        using CookieJarBrowser browser = new(host);

        // Act
        string head = await ReadHeadAsync(browser, path);

        // Assert
        AddressTagsOf(head, withImage: false).ShouldBe([$"canonical={expectedAddress}", $"og:url={expectedAddress}"]);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/regulamin")]
    public async Task Get_PublicPage_SendsItsTitleAndDescriptionAsPreviewText(string path)
    {
        // Arrange
        using WebApplicationFactory<Program> host = CreateHost(new NotFoundServer());
        using CookieJarBrowser browser = new(host);

        // Act
        string head = await ReadHeadAsync(browser, path);

        // Assert
        string title = TitlesOf(head).ShouldHaveSingleItem();
        string description = MetaContents(head, "name", "description").ShouldHaveSingleItem();
        string[] previewText =
        [
            .. MetaContents(head, "property", "og:title").Select(content => $"og:title={content}"),
            .. MetaContents(head, "property", "og:description").Select(content => $"og:description={content}")
        ];
        description.ShouldNotBeNullOrWhiteSpace();
        previewText.ShouldBe([$"og:title={title}", $"og:description={description}"]);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/regulamin")]
    [InlineData("/no-such-page")]
    public async Task Get_AnyPage_SendsTheSiteWidePreviewTags(string path)
    {
        // Arrange
        using WebApplicationFactory<Program> host = CreateHost(new NotFoundServer());
        using CookieJarBrowser browser = new(host);

        // Act
        string head = await ReadHeadAsync(browser, path);

        // Assert
        string[] tags =
        [
            .. SiteWideKeys.Select(key => $"{key.Attribute} {key.Name}={string.Join('|', MetaContents(head, key.Attribute, key.Name))}")
        ];
        tags.ShouldBe(
        [
            "property og:site_name=lotro-translator.pl",
            "property og:locale=pl_PL",
            "property og:type=website",
            "property og:image:width=300",
            "property og:image:height=300",
            "property og:image:alt=Logo Polskie LOTRO",
            "name twitter:card=summary"
        ]);
        // The fingerprint changes with the file, so Discord and Facebook drop a preview they cached.
        MetaContents(head, "property", "og:image").ShouldHaveSingleItem().ShouldMatch(@"^https://localhost/og-logo\.[a-z0-9]+\.png$");
    }

    [Fact]
    public async Task Get_PreviewImage_IsAPngOfTheAdvertisedSizeOnThePageOrigin()
    {
        // Arrange
        using WebApplicationFactory<Program> host = CreateHost(new NotFoundServer());
        using CookieJarBrowser browser = new(host);
        string head = await ReadHeadAsync(browser, "/");
        Uri image = new(MetaContents(head, "property", "og:image").Single());
        string advertisedSize =
            $"{MetaContents(head, "property", "og:image:width").Single()}x{MetaContents(head, "property", "og:image:height").Single()}";

        // Act
        using HttpResponseMessage response = await browser.GetAsync(image.PathAndQuery);
        byte[] file = await response.Content.ReadAsByteArrayAsync();

        // Assert
        image.GetLeftPart(UriPartial.Authority).ShouldBe("https://localhost");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("image/png");
        file[..8].ShouldBe(PngSignature);
        // The IHDR chunk follows the signature: its width and height are big-endian at bytes 16 and 20.
        $"{BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(16))}x{BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(20))}"
            .ShouldBe(advertisedSize);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/regulamin")]
    public async Task Get_PageOnAnotherHost_GivesThatHostInEveryAddress(string path)
    {
        // Arrange
        using WebApplicationFactory<Program> host = CreateHost(new NotFoundServer());
        using CookieJarBrowser browser = new(host);
        HttpRequestMessage request = new(HttpMethod.Get, path);
        request.Headers.Host = OtherHost;

        // Act
        using HttpResponseMessage response = await browser.SendAsync(request);
        string head = HeadOf(await response.Content.ReadAsStringAsync());

        // Assert
        string[] addresses = AddressTagsOf(head, withImage: true);
        addresses.Select(address => address.Split('=', 2)[0]).ShouldBe(["canonical", "og:url", "og:image"]);
        addresses.ShouldAllBe(address => address.Split('=', 2)[1].StartsWith($"https://{OtherHost}/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Get_HomePageWhileItsDataLoads_SendsThePreviewHeadInTheFirstFrame()
    {
        // Arrange
        using HeldServer server = new();
        using WebApplicationFactory<Program> host = CreateHost(server);
        using HttpClient client = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = BaseAddress
        });
        // Only a guard against a hung run: the first frame comes long before it.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));

        // Act
        using HttpResponseMessage response = await client.GetAsync("/", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        using StreamReader body = new(await response.Content.ReadAsStreamAsync(timeout.Token));
        string firstFrame = await ReadUntilAsync(body, "</head>", timeout.Token);
        server.Release();
        string update = await body.ReadToEndAsync(timeout.Token);

        // Assert
        string head = HeadOf(firstFrame);
        TitlesOf(head).ShouldBe([HomeTitle]);
        AddressTagsOf(head, withImage: true).Select(address => address.Split('=', 2)[0])
            .ShouldBe(["canonical", "og:url", "og:image"]);
        MetaContents(head, "property", "og:title").ShouldBe([HomeTitle]);
        update.ShouldContain("<blazor-ssr>");
    }

    public LinkPreviewTagsTests(ProductionFrontendFactory factory)
    {
        _factory = factory;
    }

    private WebApplicationFactory<Program> CreateHost(HttpMessageHandler translationSystem) =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient(DashboardTranslationSystem.ClientName)
                .ConfigurePrimaryHttpMessageHandler(() => translationSystem)));

    private static async Task<string> ReadHeadAsync(CookieJarBrowser browser, string path)
    {
        using HttpResponseMessage response = await browser.GetAsync(path);

        return HeadOf(await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> ReadUntilAsync(StreamReader body, string marker, CancellationToken cancellationToken)
    {
        StringBuilder text = new();
        char[] buffer = new char[4096];
        while (!text.ToString().Contains(marker, StringComparison.Ordinal))
        {
            int read = await body.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                throw new InvalidOperationException($"The page ended before {marker}.");
            }

            text.Append(buffer, 0, read);
        }

        return text.ToString();
    }

    private static string HeadOf(string markup)
    {
        Match head = HeadRegex().Match(markup);

        return head.Success ? head.Groups["head"].Value : throw new InvalidOperationException("The page has no head.");
    }

    private static string[] TitlesOf(string head) =>
        [.. TitleRegex().Matches(head).Select(match => WebUtility.HtmlDecode(match.Groups["title"].Value))];

    /// <summary>Each absolute address in the head as "tag=address", so a tag sent twice or missing shows.</summary>
    private static string[] AddressTagsOf(string head, bool withImage) =>
    [
        .. CanonicalLinkRegex().Matches(head).Select(match => $"canonical={WebUtility.HtmlDecode(match.Groups["href"].Value)}"),
        .. MetaContents(head, "property", "og:url").Select(address => $"og:url={address}"),
        .. withImage ? MetaContents(head, "property", "og:image").Select(address => $"og:image={address}") : []
    ];

    /// <summary>
    /// The content of every meta tag with that key under that attribute, so a tag written under the wrong
    /// attribute (<c>name</c> for an OpenGraph tag) is missing here.
    /// </summary>
    private static string[] MetaContents(string head, string attribute, string key) =>
    [
        .. MetaTagRegex().Matches(head)
            .Where(match => match.Groups["attribute"].Value == attribute
                && WebUtility.HtmlDecode(match.Groups["key"].Value) == key)
            .Select(match => WebUtility.HtmlDecode(match.Groups["content"].Value))
    ];

    [GeneratedRegex("<head>(?<head>.*?)</head>", RegexOptions.Singleline)]
    private static partial Regex HeadRegex();

    [GeneratedRegex("<title>(?<title>[^<]*)</title>")]
    private static partial Regex TitleRegex();

    [GeneratedRegex("""<link\b[^>]*\brel="canonical"[^>]*\bhref="(?<href>[^"]*)""")]
    private static partial Regex CanonicalLinkRegex();

    [GeneratedRegex("""<meta\b[^>]*?\b(?<attribute>property|name)="(?<key>[^"]*)"[^>]*?\bcontent="(?<content>[^"]*)""")]
    private static partial Regex MetaTagRegex();

    /// <summary>
    /// A translation API that holds every call until the test releases it, so the home page has sent its
    /// first frame and waits for its data. It answers 404, so the page then shows its calm notice.
    /// </summary>
    private sealed class HeldServer : HttpMessageHandler
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release()
        {
            _released.TrySetResult();
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await _released.Task.WaitAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        protected override void Dispose(bool disposing)
        {
            // A test that fails before its release must not leave the page's call waiting.
            Release();
            base.Dispose(disposing);
        }
    }
}
