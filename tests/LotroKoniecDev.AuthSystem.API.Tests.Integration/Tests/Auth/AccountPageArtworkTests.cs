using System.Text.RegularExpressions;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// MoFin's logo, the tab icon and his credit on every account page (#1371). The pages come from
/// <see cref="AccountPages"/>, so a new page is checked the day it is added.
/// </summary>
public sealed partial class AccountPageArtworkTests : EndpointsTestBase
{
    private const string Logo = """<img class="brand-mark" src="/logo.png" alt="" width="30" height="34" />""";
    private const string TabIcon = """<link rel="icon" href="/favicon.ico" />""";
    private const string Credit = """<p class="foot-credit">Logo i grafiki: MoFin</p>""";

    [Theory]
    [MemberData(nameof(AccountPages.All), MemberType = typeof(AccountPages))]
    public async Task AccountPage_Rendered_ShowsTheLogoInTheBrandLinkInsteadOfTheCircle(string page)
    {
        // Act
        string html = await ReadPageAsync(page);

        // Assert
        string brandLink = BrandLinkRegex().Match(html).Groups["inner"].Value;
        brandLink.ShouldContain(Logo);
        brandLink.ShouldNotContain("<svg");
    }

    [Theory]
    [MemberData(nameof(AccountPages.All), MemberType = typeof(AccountPages))]
    public async Task AccountPage_Rendered_LinksTheTabIconInTheHead(string page)
    {
        // Act
        string html = await ReadPageAsync(page);

        // Assert
        HeadRegex().Match(html).Groups["inner"].Value.ShouldContain(TabIcon);
    }

    [Theory]
    [MemberData(nameof(AccountPages.All), MemberType = typeof(AccountPages))]
    public async Task AccountPage_Rendered_CreditsTheArtistInTheFooter(string page)
    {
        // Act
        string html = await ReadPageAsync(page);

        // Assert
        FooterRegex().Match(html).Groups["inner"].Value.ShouldContain(Credit);
    }

    [Theory]
    [MemberData(nameof(AccountPages.All), MemberType = typeof(AccountPages))]
    public async Task AccountPage_Rendered_ServesEveryImageAndIconItNames(string page)
    {
        // Arrange
        string html = await ReadPageAsync(page);
        string[] paths =
        [
            .. ImageSourceRegex().Matches(html).Select(match => match.Groups["path"].Value),
            .. IconLinkRegex().Matches(html).Select(match => match.Groups["path"].Value)
        ];
        List<string> answers = [];

        // Act
        foreach (string path in paths)
        {
            using HttpResponseMessage response = await ApiClient.Http.GetAsync(new Uri(path, UriKind.Relative));
            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            answers.Add($"{path}: {(int)response.StatusCode} {mediaType?.Split('/')[0]}");
        }

        // Assert
        paths.ShouldNotBeEmpty();
        answers.ShouldAllBe(answer => answer.EndsWith(" 200 image", StringComparison.Ordinal));
    }

    public AccountPageArtworkTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    private async Task<string> ReadPageAsync(string page)
    {
        using HttpResponseMessage response = await ApiClient.Http.GetAsync(new Uri(page, UriKind.Relative));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync();
    }

    [GeneratedRegex("""<a\b[^>]*\bclass="brand"[^>]*>(?<inner>.*?)</a>""", RegexOptions.Singleline)]
    private static partial Regex BrandLinkRegex();

    [GeneratedRegex("""<head>(?<inner>.*?)</head>""", RegexOptions.Singleline)]
    private static partial Regex HeadRegex();

    [GeneratedRegex("""<footer class="foot">(?<inner>.*?)</footer>""", RegexOptions.Singleline)]
    private static partial Regex FooterRegex();

    [GeneratedRegex("""<img\b[^>]*\bsrc="(?<path>[^"]+)""")]
    private static partial Regex ImageSourceRegex();

    [GeneratedRegex("""<link\b[^>]*\brel="icon"[^>]*\bhref="(?<path>[^"]+)""")]
    private static partial Regex IconLinkRegex();
}
