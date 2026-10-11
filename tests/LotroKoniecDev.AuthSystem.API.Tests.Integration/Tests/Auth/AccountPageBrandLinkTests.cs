using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using LotroKoniecDev.AuthSystem.API.BackgroundServices;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Settings;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// The logo on every account page linked to production, so a tester on staging who clicked it went on
/// testing production without noticing (#1074). The pages come from <see cref="AccountPages"/>, so a new
/// page is checked the day it is added.
/// </summary>
public sealed partial class AccountPageBrandLinkTests : EndpointsTestBase
{
    [Theory]
    [MemberData(nameof(AccountPages.All), MemberType = typeof(AccountPages))]
    public async Task AccountPage_WebClientConfigured_LinksTheLogoToItsWebsite(string page)
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
    [MemberData(nameof(AccountPages.All), MemberType = typeof(AccountPages))]
    public async Task AccountPage_TestHostOnLocalhost_ContainsNoProductionAddress(string page)
    {
        // Act
        using HttpResponseMessage response = await ApiClient.Http.GetAsync(new Uri(page, UriKind.Relative));

        // Assert: every web address this host is given is on localhost, so a production link can only come from the page
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = await response.Content.ReadAsStringAsync();
        ProductionAddressRegex().Matches(html).Select(match => match.Value).ShouldBeEmpty();
    }

    [Fact]
    public async Task AccountPages_WebClientNotConfigured_ShowTheLogoWithoutALink()
    {
        // Arrange: the seeder needs the web client's addresses, so only the logo's source loses them
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
        List<string> renderedPages = [];

        // Act
        foreach (string page in AccountPages.Paths)
        {
            using HttpResponseMessage response = await client.GetAsync(new Uri(page, UriKind.Relative));
            string html = await response.Content.ReadAsStringAsync();
            IEnumerable<string> brandLinks = BrandLinkRegex().Matches(html).Select(match => match.Value);
            renderedPages.Add($"{page}: {(int)response.StatusCode} {string.Join(" ", brandLinks)}");
        }

        // Assert: no href at all, so the logo can never lead to the API discovery JSON at this host's root
        renderedPages.ShouldBe(AccountPages.Paths.Select(page => $"{page}: 200 <a class=\"brand\">"));
    }

    public AccountPageBrandLinkTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [GeneratedRegex("""<a\b[^>]*\bclass="brand"[^>]*>""")]
    private static partial Regex BrandLinkRegex();

    /// <summary>
    /// Any link to the production domain or one of its subdomains, with or without a scheme. The bare
    /// name in titles and footers is the service's name, not an address, so it is not matched.
    /// </summary>
    [GeneratedRegex(@"(?:https?:)?//(?:[a-z0-9-]+\.)*lotro-translator\.pl", RegexOptions.IgnoreCase)]
    private static partial Regex ProductionAddressRegex();
}
