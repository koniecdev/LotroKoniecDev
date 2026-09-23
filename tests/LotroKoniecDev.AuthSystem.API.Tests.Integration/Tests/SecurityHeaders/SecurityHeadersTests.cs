using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>
    /// Every account page, found by reflection so a theory can list them. The check below proves the
    /// list matches the pages the app really maps, so a new page cannot slip past these tests.
    /// </summary>
    private static readonly IReadOnlyList<string> AccountPagePaths = typeof(Program).Assembly.GetTypes()
        .Where(type => type.IsSubclassOf(typeof(PageModel))
            && type is { Namespace: string pageNamespace }
            && pageNamespace.EndsWith(".Pages.Account", StringComparison.Ordinal))
        .Select(type => "/Account/" + type.Name[..^"Model".Length])
        .Order(StringComparer.Ordinal)
        .ToList();

    public SecurityHeadersTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    public static TheoryData<string> AccountPages { get; } = new(AccountPagePaths);

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
        IReadOnlyList<string> listedPages = AccountPagePaths;

        // Assert
        listedPages.ShouldNotBeEmpty();
        listedPages.ShouldBe(mappedPages);
    }

    [Theory]
    [MemberData(nameof(AccountPages))]
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
    /// console says so. The pages keep their styles inline, so every block needs this response's nonce.
    /// </summary>
    [Theory]
    [MemberData(nameof(AccountPages))]
    public async Task AccountPage_ShouldPutTheResponsesNonceOnEveryInlineStyle(string page)
    {
        // Act
        using HttpResponseMessage response = await ApiClient.Http.GetAsync(new Uri(page, UriKind.Relative));

        // Assert
        string nonce = StyleNonce(SingleHeader(response, "Content-Security-Policy")).ShouldNotBeNull();
        string html = await response.Content.ReadAsStringAsync();
        List<string> styleTags = StyleTagRegex().Matches(html).Select(match => match.Value).ToList();
        styleTags.ShouldNotBeEmpty();
        styleTags.ShouldAllBe(styleTag => styleTag.Contains($"nonce=\"{nonce}\"", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(AccountPages))]
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
    [MemberData(nameof(AccountPages))]
    public async Task AccountPage_ShouldServeNoStyleAttribute(string page)
    {
        // Act
        using HttpResponseMessage response = await ApiClient.Http.GetAsync(new Uri(page, UriKind.Relative));

        // Assert: a nonce covers a style block, never a style attribute
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

    [Fact]
    public async Task EachResponse_ShouldGetItsOwnNonce()
    {
        // Act
        using HttpResponseMessage first = await ApiClient.Http.GetAsync(new Uri("/Account/Login", UriKind.Relative));
        using HttpResponseMessage second = await ApiClient.Http.GetAsync(new Uri("/Account/Login", UriKind.Relative));

        // Assert
        string firstNonce = StyleNonce(SingleHeader(first, "Content-Security-Policy")).ShouldNotBeNull();
        string secondNonce = StyleNonce(SingleHeader(second, "Content-Security-Policy")).ShouldNotBeNull();
        firstNonce.ShouldNotBe(secondNonce);
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
        script.Content.Headers.ContentType?.MediaType.ShouldBe("text/javascript");
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
    public async Task ThrottledPage_ShouldCarryTheNonceItsStyleNeeds()
    {
        // Arrange: the 429 page is written by the limiter, outside Razor, so it gets the nonce by hand
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
        string nonce = StyleNonce(SingleHeader(lastResponse, "Content-Security-Policy")).ShouldNotBeNull();
        string html = await lastResponse.Content.ReadAsStringAsync();
        html.ShouldContain($"<style nonce=\"{nonce}\">");
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

    private static string? StyleNonce(string? contentSecurityPolicy)
    {
        if (contentSecurityPolicy is null)
        {
            return null;
        }

        Match match = StyleNonceRegex().Match(contentSecurityPolicy);
        return match.Success ? match.Groups["nonce"].Value : null;
    }

    [GeneratedRegex(@"style-src [^;]*'nonce-(?<nonce>[A-Za-z0-9_-]+)'")]
    private static partial Regex StyleNonceRegex();

    [GeneratedRegex("<style[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex StyleTagRegex();

    [GeneratedRegex("<script[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptTagRegex();

    [GeneratedRegex(@"<[a-z][^>]*\sstyle\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex StyleAttributeRegex();

    [GeneratedRegex(@"<[a-z][^>]*\son[a-z]+\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex EventHandlerAttributeRegex();
}
