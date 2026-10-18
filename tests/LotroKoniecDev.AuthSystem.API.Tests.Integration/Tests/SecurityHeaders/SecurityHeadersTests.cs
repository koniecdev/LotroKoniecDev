using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.SecurityHeaders;

/// <summary>
/// The auth origin used to send only HSTS. Its frame protection was an accident of antiforgery: a page
/// got <c>SAMEORIGIN</c> only when it rendered a form (#693). These tests hold the policy that replaced it,
/// on every account page and on the responses around them.
/// The pages are listed by reflection rather than by hand, and a test proves that list equals the
/// Razor pages the app maps, so a new page is checked the day it is added.
/// </summary>
public sealed partial class SecurityHeadersTests : EndpointsTestBase
{
    private const int ForgotPasswordPermitLimit = 3;
    private const string AccountStylesheetLink = "<link rel=\"stylesheet\" href=\"/account.css\" />";

    public SecurityHeadersTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public void AccountPages_ShouldBeEveryRazorPageTheAppMaps()
    {
        // Arrange
        IReadOnlyList<string> mappedPages = Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<PageActionDescriptor>() is not null)
            .Select(endpoint => "/" + endpoint.RoutePattern.RawText?.TrimStart('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToList();

        // Act
        IReadOnlyList<string> listedPages = AccountPages.Paths;

        // Assert
        listedPages.ShouldNotBeEmpty();
        listedPages.ShouldBe(mappedPages);
    }

    [Theory]
    [MemberData(nameof(AccountPages.All), MemberType = typeof(AccountPages))]
    public async Task AccountPage_ShouldCarryTheSecurityHeaders(string page)
    {
        // Act
        using HttpResponseMessage response = await ApiClient.Http.GetAsync(new Uri(page, UriKind.Relative));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        SingleHeader(response, "Content-Security-Policy").ShouldNotBeNull().ShouldContain("frame-ancestors 'none'");
        HeaderValues(response, "X-Frame-Options").ShouldBe(["DENY"]);
        SingleHeader(response, "X-Content-Type-Options").ShouldBe("nosniff");
        SingleHeader(response, "Referrer-Policy").ShouldBe("no-referrer");
    }

    /// <summary>
    /// The #670 lesson: a CSP that blocks the page's own markup still returns 200, and only the browser
    /// console says so. style-src is 'self', so the styles have to come from the shared file (#833).
    /// </summary>
    [Theory]
    [MemberData(nameof(AccountPages.All), MemberType = typeof(AccountPages))]
    public async Task AccountPage_ShouldLoadItsStylesFromTheSharedStylesheet(string page)
    {
        // Act
        using HttpResponseMessage response = await ApiClient.Http.GetAsync(new Uri(page, UriKind.Relative));

        // Assert
        SingleHeader(response, "Content-Security-Policy").ShouldNotBeNull().Split("; ").ShouldContain("style-src 'self'");
        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain(AccountStylesheetLink);
        StyleTagRegex().IsMatch(html).ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(AccountPages.All), MemberType = typeof(AccountPages))]
    public async Task AccountPage_ShouldServeNoInlineScript(string page)
    {
        // Act
        using HttpResponseMessage response = await ApiClient.Http.GetAsync(new Uri(page, UriKind.Relative));

        // Assert: script-src is 'self' with no nonce, so a script block or an on*= handler is blocked
        string html = await response.Content.ReadAsStringAsync();
        ScriptTagRegex().Matches(html).Select(match => match.Value)
            .ShouldAllBe(scriptTag => scriptTag.Contains("src=", StringComparison.OrdinalIgnoreCase));
        EventHandlerAttributeRegex().IsMatch(html).ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(AccountPages.All), MemberType = typeof(AccountPages))]
    public async Task AccountPage_ShouldServeNoStyleAttribute(string page)
    {
        // Act
        using HttpResponseMessage response = await ApiClient.Http.GetAsync(new Uri(page, UriKind.Relative));

        // Assert: style-src 'self' blocks a style attribute as well
        string html = await response.Content.ReadAsStringAsync();
        StyleAttributeRegex().IsMatch(html).ShouldBeFalse();
    }

    /// <summary>
    /// The acceptance check of #693: these responses render no antiforgery token, so before the change
    /// they carried no frame header at all. The two link pages render no form when the link is missing.
    /// </summary>
    [Theory]
    [InlineData("/Account/ConfirmEmail")]
    [InlineData("/Account/ConfirmEmailChange")]
    [InlineData("/Account/PrivacyPolicy")]
    [InlineData("/Account/RevertEmailChange")]
    [InlineData("/Account/CancelDeletion")]
    public async Task PageWithoutAForm_ShouldStillForbidFraming(string page)
    {
        // Act
        using HttpResponseMessage response = await ApiClient.Http.GetAsync(new Uri(page, UriKind.Relative));

        // Assert
        string html = await response.Content.ReadAsStringAsync();
        html.ShouldNotContain("__RequestVerificationToken");
        HeaderValues(response, "X-Frame-Options").ShouldBe(["DENY"]);
        SingleHeader(response, "Content-Security-Policy").ShouldNotBeNull().ShouldContain("frame-ancestors 'none'");
    }

    [Fact]
    public async Task PageWithAForm_ShouldSendOneFrameHeader_WhichIsDeny()
    {
        // Act: antiforgery adds SAMEORIGIN while it renders the token; ours must be the only value left
        using HttpResponseMessage response =
            await ApiClient.Http.GetAsync(new Uri("/Account/Login", UriKind.Relative));

        // Assert
        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("__RequestVerificationToken");
        HeaderValues(response, "X-Frame-Options").ShouldBe(["DENY"]);
    }

    [Theory]
    [InlineData("/account.css", ".auth-panel")]
    [InlineData("/error-page.css", "main {")]
    public async Task Stylesheet_ShouldBeServedFromThisOrigin(string path, string expectedRule)
    {
        // Act
        using HttpResponseMessage response = await ApiClient.Http.GetAsync(new Uri(path, UriKind.Relative));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/css");
        string body = await response.Content.ReadAsStringAsync();
        body.ShouldContain(expectedRule);
    }

    /// <summary>
    /// The point of #833: the pages used to send their styles again on every visit, because a page is
    /// never cached. The shared file is checked with its ETag and not downloaded again.
    /// </summary>
    [Fact]
    public async Task AccountStylesheet_ShouldAnswerARepeatVisitWithNotModified()
    {
        // Arrange
        using HttpResponseMessage first = await ApiClient.Http.GetAsync(new Uri("/account.css", UriKind.Relative));
        EntityTagHeaderValue etag = first.Headers.ETag.ShouldNotBeNull();
        using HttpRequestMessage repeat = new(HttpMethod.Get, new Uri("/account.css", UriKind.Relative));
        repeat.Headers.IfNoneMatch.Add(etag);

        // Act
        using HttpResponseMessage response = await ApiClient.Http.SendAsync(repeat);

        // Assert
        first.Headers.CacheControl?.NoStore.ShouldNotBe(true);
        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
    }

    /// <summary>
    /// A successful sign-in POST ends in a redirect to the frontend's callback, and Chrome checks every
    /// redirect of a form submission against <c>form-action</c>. Without the frontend origin, sign-in breaks.
    /// </summary>
    [Fact]
    public async Task LoginPagePolicy_ShouldLetTheSignInEndOnTheFrontend()
    {
        // Act
        using HttpResponseMessage response =
            await ApiClient.Http.GetAsync(new Uri("/Account/Login", UriKind.Relative));

        // Assert
        string csp = SingleHeader(response, "Content-Security-Policy").ShouldNotBeNull();
        csp.Split("; ").ShouldContain($"form-action 'self' {AuthSystemApiFactory.TestFrontendAppRoot}");
    }

    [Fact]
    public async Task LoginScript_ShouldBeServedFromThisOrigin()
    {
        // Act
        using HttpResponseMessage page = await ApiClient.Http.GetAsync(new Uri("/Account/Login", UriKind.Relative));
        using HttpResponseMessage script = await ApiClient.Http.GetAsync(new Uri("/login.js", UriKind.Relative));

        // Assert: the password toggle moved out of the page, and the file it moved to is really there
        string html = await page.Content.ReadAsStringAsync();
        html.ShouldContain("<script src=\"/login.js\"></script>");
        script.StatusCode.ShouldBe(HttpStatusCode.OK);
        script.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("text/javascript");
        string body = await script.Content.ReadAsStringAsync();
        body.ShouldContain("password-toggle");
    }

    /// <summary>
    /// Every response, not only the pages: JSON, a static file, and a 404 that the status-code pages
    /// write after routing found nothing.
    /// </summary>
    [Theory]
    [InlineData("/health/live", HttpStatusCode.OK)]
    [InlineData("/", HttpStatusCode.OK)]
    [InlineData("/login.js", HttpStatusCode.OK)]
    [InlineData("/account.css", HttpStatusCode.OK)]
    [InlineData("/does-not-exist", HttpStatusCode.NotFound)]
    public async Task NonPageResponse_ShouldCarryTheSecurityHeadersToo(string path, HttpStatusCode expectedStatus)
    {
        // Act
        using HttpResponseMessage response = await ApiClient.Http.GetAsync(new Uri(path, UriKind.Relative));

        // Assert
        response.StatusCode.ShouldBe(expectedStatus);
        SingleHeader(response, "X-Content-Type-Options").ShouldBe("nosniff");
        SingleHeader(response, "X-Frame-Options").ShouldBe("DENY");
        SingleHeader(response, "Content-Security-Policy").ShouldNotBeNull();
    }

    [Fact]
    public async Task ThrottledPage_ShouldLoadItsStylesFromAFile()
    {
        // Arrange: the limiter writes the 429 page outside Razor, under the same CSP as every page
        using WebApplicationFactory<Program> limitedHost = Factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configBuilder) =>
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "RateLimiting:ForceEnable", "true" }
                })));
        using HttpClient client = limitedHost.CreateClient();

        HttpResponseMessage? lastResponse = null;

        // Act
        for (int i = 0; i < ForgotPasswordPermitLimit + 1; i++)
        {
            lastResponse?.Dispose();
            using FormUrlEncodedContent send = new(new Dictionary<string, string>
            {
                ["Email"] = $"csp-429-{i}@lotro-translator.pl"
            });
            using HttpRequestMessage request = new(HttpMethod.Post, new Uri("/Account/ForgotPassword", UriKind.Relative));
            request.Content = send;
            request.Headers.Accept.ParseAdd("text/html");
            lastResponse = await client.SendAsync(request);
        }

        // Assert
        lastResponse.ShouldNotBeNull();
        lastResponse.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        SingleHeader(lastResponse, "Content-Security-Policy").ShouldNotBeNull().Split("; ").ShouldContain("style-src 'self'");
        string html = await lastResponse.Content.ReadAsStringAsync();
        html.ShouldContain("<link rel=\"stylesheet\" href=\"/error-page.css\" />");
        StyleTagRegex().IsMatch(html).ShouldBeFalse();
        SingleHeader(lastResponse, "X-Frame-Options").ShouldBe("DENY");
        lastResponse.Dispose();
    }

    private static IReadOnlyList<string> HeaderValues(HttpResponseMessage response, string name)
    {
        return response.Headers.TryGetValues(name, out IEnumerable<string>? values)
            ? values.ToList()
            : [];
    }

    private static string? SingleHeader(HttpResponseMessage response, string name)
    {
        return HeaderValues(response, name) is [string value] ? value : null;
    }

    [GeneratedRegex("<style[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex StyleTagRegex();

    [GeneratedRegex("<script[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptTagRegex();

    [GeneratedRegex(@"<[a-z][^>]*\sstyle\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex StyleAttributeRegex();

    [GeneratedRegex(@"<[a-z][^>]*\son[a-z]+\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex EventHandlerAttributeRegex();
}
