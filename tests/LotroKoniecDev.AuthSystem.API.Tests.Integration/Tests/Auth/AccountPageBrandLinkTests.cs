using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using LotroKoniecDev.AuthSystem.API.BackgroundServices;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Settings;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.SecurityHeaders;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// The logo on every account page linked to production, so a tester on staging who clicked it went on
/// testing production without noticing (#1074). The page list is the one in
/// <see cref="SecurityHeadersTests"/>, and a test there holds it equal to the pages the app maps, so a new
/// page is checked the day it is added.
/// </summary>
public sealed partial class AccountPageBrandLinkTests : EndpointsTestBase
{
    public AccountPageBrandLinkTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Theory]
    [MemberData(nameof(SecurityHeadersTests.AccountPages), MemberType = typeof(SecurityHeadersTests))]
    public async Task AccountPage_ShouldLinkTheLogoToTheConfiguredWebsite(string page)
    {
        // Act
        using HttpResponseMessage response = await ApiClient.Http.GetAsync(new Uri(page, UriKind.Relative));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = await response.Content.ReadAsStringAsync();
        BrandLinkRegex().Matches(html).Select(match => match.Value)
            .ShouldBe([$"<a href=\"{AuthSystemApiFactory.TestFrontendAppRoot}/\" class=\"brand\">"]);
    }

    [Theory]
    [MemberData(nameof(SecurityHeadersTests.AccountPages), MemberType = typeof(SecurityHeadersTests))]
    public async Task AccountPage_ShouldContainNoProductionAddress(string page)
    {
        // Act
        using HttpResponseMessage response = await ApiClient.Http.GetAsync(new Uri(page, UriKind.Relative));

        // Assert: the test host points at localhost, so a production address can only be written into the page
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = await response.Content.ReadAsStringAsync();
        ProductionAddressRegex().Matches(html).Select(match => match.Value).ShouldBeEmpty();
    }

    [Fact]
    public async Task AccountPages_ShouldShowTheLogoWithoutALink_WhenTheWebClientIsNotConfigured()
    {
        // Arrange
        await using WebApplicationFactory<Program> host = Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                // A second relay on this database could take a row another test waits for.
                AuthSystemApiFactory.RemoveHostedService<OutboxRelay>(services);

                OpenIddictSettings withoutWebClient = new() { Issuer = "https://localhost:5002" };
                FrontendHome frontendHome = new(Microsoft.Extensions.Options.Options.Create(withoutWebClient));
                AuthSystemApiFactory.ReplaceSingleton(services, frontendHome);
            }));
        using HttpClient client = host.CreateClient();
        List<string> brandLinks = [];

        // Act
        foreach (string page in SecurityHeadersTests.AccountPagePaths)
        {
            using HttpResponseMessage response = await client.GetAsync(new Uri(page, UriKind.Relative));
            string html = await response.Content.ReadAsStringAsync();
            brandLinks.AddRange(BrandLinkRegex().Matches(html).Select(match => $"{page}: {match.Value}"));
        }

        // Assert: no href at all, so the logo can never lead to the API discovery JSON at this host's root
        brandLinks.ShouldBe(SecurityHeadersTests.AccountPagePaths.Select(page => $"{page}: <a class=\"brand\">"));
    }

    [GeneratedRegex("""<a\b[^>]*\bclass="brand"[^>]*>""")]
    private static partial Regex BrandLinkRegex();

    [GeneratedRegex(@"https?://(?:[a-z0-9-]+\.)*lotro-translator\.pl", RegexOptions.IgnoreCase)]
    private static partial Regex ProductionAddressRegex();
}
