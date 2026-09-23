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
/// The pages are listed from the endpoint data source rather than by hand, so a new page is checked
/// the day it is added.
/// </summary>
public sealed partial class SecurityHeadersTests : EndpointsTestBase
{
    /// <summary>The account pages today. The enumerated tests fail if they find fewer.</summary>
    private const int KnownAccountPageCount = 10;

    private const int ForgotPasswordPermitLimit = 3;

    public SecurityHeadersTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task EveryAccountPage_ShouldCarryTheSecurityHeaders()
    {
        // Arrange
        IReadOnlyList<string> pages = AccountPagePaths();
        List<string> failures = [];

        // Act
        foreach (string page in pages)
        {
            using HttpResponseMessage response = await ApiClient.Http.GetAsync(new Uri(page, UriKind.Relative));
            string? csp = SingleHeader(response, "Content-Security-Policy");

            if (csp is null || !csp.Contains("frame-ancestors 'none'", StringComparison.Ordinal))
            {
                failures.Add($"{page}: CSP '{csp}'");
            }

            if (SingleHeader(response, "X-Frame-Options") != "DENY")
            {
                failures.Add($"{page}: X-Frame-Options '{string.Join(", ", HeaderValues(response, "X-Frame-Options"))}'");
            }

            if (SingleHeader(response, "X-Content-Type-Options") != "nosniff")
            {
                failures.Add($"{page}: X-Content-Type-Options missing");
            }

            if (SingleHeader(response, "Referrer-Policy") != "no-referrer")
            {
                failures.Add($"{page}: Referrer-Policy missing");
            }
        }

        // Assert
        pages.Count.ShouldBeGreaterThanOrEqualTo(KnownAccountPageCount);
        failures.ShouldBeEmpty();
    }

    /// <summary>
    /// The #670 lesson: a CSP that blocks the page's own markup still returns 200, and only the browser
    /// console says so. Every inline style has to carry this response's nonce, and no page may carry an
    /// inline script at all.
    /// </summary>
    [Fact]
    public async Task EveryAccountPage_ShouldServeOnlyInlineContentItsOwnCspAdmits()
    {
        // Arrange
        IReadOnlyList<string> pages = AccountPagePaths();
        List<string> failures = [];

        // Act
        foreach (string page in pages)
        {
            using HttpResponseMessage response = await ApiClient.Http.GetAsync(new Uri(page, UriKind.Relative));
            string html = await response.Content.ReadAsStringAsync();
            string? nonce = StyleNonce(SingleHeader(response, "Content-Security-Policy"));

            MatchCollection styleTags = StyleTagRegex().Matches(html);
            if (styleTags.Count == 0)
            {
                failures.Add($"{page}: no <style> block found, so this check proves nothing");
            }

            foreach (Match styleTag in styleTags)
            {
                if (nonce is null || !styleTag.Value.Contains($"nonce=\"{nonce}\"", StringComparison.Ordinal))
                {
                    failures.Add($"{page}: {styleTag.Value} does not carry the header's nonce '{nonce}'");
                }
            }

            foreach (Match scriptTag in ScriptTagRegex().Matches(html))
            {
                if (!scriptTag.Value.Contains("src=", StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"{page}: inline {scriptTag.Value}, which script-src 'self' blocks");
                }
            }

            if (StyleAttributeRegex().IsMatch(html))
            {
                failures.Add($"{page}: an inline style attribute, which a nonce does not cover");
            }

            if (EventHandlerAttributeRegex().IsMatch(html))
            {
                failures.Add($"{page}: an inline event handler attribute, which script-src 'self' blocks");
            }
        }

        // Assert
        pages.Count.ShouldBeGreaterThanOrEqualTo(KnownAccountPageCount);
        failures.ShouldBeEmpty();
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

    private IReadOnlyList<string> AccountPagePaths()
    {
        return Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<PageActionDescriptor>() is not null)
            .Select(endpoint => "/" + endpoint.RoutePattern.RawText?.TrimStart('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToList();
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
