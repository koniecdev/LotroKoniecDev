using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.SharedKernel.Authorization;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.ErrorPages;

/// <summary>
/// A browser that gets a 4xx answer from the auth origin reads a Polish page, and an API client keeps the
/// problem details it can parse (#879). The 5xx page is checked next to the failures that cause it (#867).
/// The same holds for a sign-in or sign-out link that OpenIddict refuses (#912).
/// </summary>
public sealed partial class BrowserErrorPagesTests : EndpointsTestBase
{
    private const string BrowserAccept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
    private const string LoginPage = "/Account/Login";
    private const string ValidCallback = "https://localhost:5001/callback";
    private const string ForeignCallback = "https://attacker.example/callback";
    private const string CallerState = "caller-state-912";

    /// <summary>The example challenge from RFC 7636. Only its shape matters here.</summary>
    private const string PkceChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    public BrowserErrorPagesTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task UnknownAddress_ShouldShowABrowserThePolishNotFoundPage()
    {
        // Arrange
        using HttpClient browser = CreateClientWithoutRedirects();

        // Act
        using HttpResponseMessage response = await GetAsync(browser, "/does-not-exist", BrowserAccept);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");
        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("<h1>Nie ma takiej strony</h1>");
        string nonce = StyleNonce(response).ShouldNotBeNull();
        html.ShouldContain($"<style nonce=\"{nonce}\">");
    }

    [Theory]
    [InlineData("application/vnd.dev-lotrokoniecdev.hateoas.json")]
    [InlineData("application/json")]
    [InlineData("*/*")]
    [InlineData(null)]
    public async Task UnknownAddress_ShouldAnswerAnApiClientWithProblemDetails(string? accept)
    {
        // Arrange: the frontend's back-channel sends the first value, and curl or fetch send */*
        using HttpClient client = CreateClientWithoutRedirects();

        // Act
        using HttpResponseMessage response = await GetAsync(client, "/does-not-exist", accept);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("status").GetInt32().ShouldBe((int)HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The case from the ticket: the login form stays open while its cookie goes away, for example because
    /// the user cleared cookies. The token in the form is fine, and the cookie it belongs to is missing.
    /// </summary>
    [Fact]
    public async Task LoginPost_ShouldShowABrowserTheFormExpiredPage_WhenTheFormsCookieIsGone()
    {
        // Arrange
        using HttpClient formLoader = CreateClientWithoutRedirects();
        string token = await LoadLoginFormTokenAsync(formLoader);
        using HttpClient browserWithoutTheCookie = CreateClientWithoutRedirects();

        // Act
        using HttpResponseMessage response = await PostLoginAsync(browserWithoutTheCookie, token, BrowserAccept);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");
        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("<h1>Formularz wygasł</h1>");
        string nonce = StyleNonce(response).ShouldNotBeNull();
        html.ShouldContain($"<style nonce=\"{nonce}\">");
    }

    [Fact]
    public async Task LoginPost_ShouldShowABrowserTheFormExpiredPage_WhenTheFormCarriesNoToken()
    {
        // Arrange
        using HttpClient browser = CreateClientWithoutRedirects();

        // Act
        using HttpResponseMessage response = await PostLoginAsync(browser, token: null, BrowserAccept);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("<h1>Formularz wygasł</h1>");
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("*/*")]
    [InlineData(null)]
    public async Task LoginPost_ShouldAnswerAnApiClientWithProblemDetails_WhenTheFormCheckFails(string? accept)
    {
        // Arrange
        using HttpClient client = CreateClientWithoutRedirects();

        // Act
        using HttpResponseMessage response = await PostLoginAsync(client, token: null, accept);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("<!DOCTYPE html>");
    }

    /// <summary>
    /// A JSON body that does not parse throws, and one of the exception handlers answers it, not the
    /// status-code pages. It is still not an expired form, so the browser gets the general page.
    /// </summary>
    [Fact]
    public async Task MalformedRequest_ShouldShowABrowserTheGeneralPage()
    {
        // Arrange
        using HttpClient browser = CreateClientWithoutRedirects();

        // Act
        using HttpResponseMessage response = await PostMalformedRegisterAsync(browser, BrowserAccept);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");
        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("<h1>Nie udało się obsłużyć żądania</h1>");
        html.ShouldNotContain("Formularz wygasł");
    }

    [Theory]
    [InlineData("application/vnd.dev-lotrokoniecdev.hateoas.json")]
    [InlineData("*/*")]
    [InlineData(null)]
    public async Task MalformedRequest_ShouldAnswerAnApiClientWithProblemDetails(string? accept)
    {
        // Arrange
        using HttpClient client = CreateClientWithoutRedirects();

        // Act
        using HttpResponseMessage response = await PostMalformedRegisterAsync(client, accept);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("errorCode").GetString().ShouldBe("Http.BadRequest");
    }

    /// <summary>
    /// An endpoint's own <c>Results.Problem</c> goes through the same writer. The frontend reads the
    /// <c>errorCode</c> from it, so an API client must keep it.
    /// </summary>
    [Fact]
    public async Task RefusedRegistration_ShouldAnswerAnApiClientWithItsErrorCode()
    {
        // Arrange
        using HttpClient client = CreateClientWithoutRedirects();

        // Act
        using HttpResponseMessage response = await PostRegisterWithoutConsentAsync(client, accept: null);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("errorCode").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task RefusedRegistration_ShouldShowABrowserTheGeneralPage()
    {
        // Arrange: no browser posts JSON here, but one that names text/html gets a page, never raw JSON
        using HttpClient browser = CreateClientWithoutRedirects();

        // Act
        using HttpResponseMessage response = await PostRegisterWithoutConsentAsync(browser, BrowserAccept);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");
        (await response.Content.ReadAsStringAsync()).ShouldContain("<h1>Nie udało się obsłużyć żądania</h1>");
    }

    /// <summary>
    /// Sign-in and sign-out links that OpenIddict refuses itself, without sending the error back to the
    /// client (#912). Each one carries a <c>state</c>, which no answer may repeat. The second value is the
    /// parameter OpenIddict names as the reason.
    /// </summary>
    /// <remarks>
    /// The row without PKCE has a valid client and callback. It is answered here too, because OpenIddict
    /// sends an error back to the client only when the whole request passed its checks.
    /// </remarks>
    public static TheoryData<string, string> BrokenSignInAndSignOutLinks => new()
    {
        { AuthorizeLink(clientId: "no-such-client", ValidCallback), "client_id" },
        { AuthorizeLink(clientId: null, ValidCallback), "client_id" },
        { AuthorizeLink(AuthConstants.ClientIds.Web, ForeignCallback), "redirect_uri" },
        { AuthorizeLink(AuthConstants.ClientIds.Web, ValidCallback, codeChallenge: null), "code_challenge" },
        { LogoutLink(ForeignCallback), "post_logout_redirect_uri" }
    };

    /// <summary>
    /// The owner's decision on #912: the general page only. The user cannot repair the link, so the page
    /// names no reason, in Polish or in English.
    /// </summary>
    [Theory]
    [MemberData(nameof(BrokenSignInAndSignOutLinks))]
    public async Task BrokenSignInOrSignOutLink_ShouldShowABrowserTheGeneralPage(string link, string rejectedParameter)
    {
        // Arrange
        using HttpClient browser = CreateClientWithoutRedirects();

        // Act
        using HttpResponseMessage response = await GetAsync(browser, link, BrowserAccept);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Headers.Location.ShouldBeNull();
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");
        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("<h1>Nie udało się obsłużyć żądania</h1>");
        html.ShouldNotContain("invalid_request");
        html.ShouldNotContain(rejectedParameter);
        html.ShouldNotContain(CallerState);
        string nonce = StyleNonce(response).ShouldNotBeNull();
        html.ShouldContain($"<style nonce=\"{nonce}\">");
    }

    [Theory]
    [MemberData(nameof(BrokenSignInAndSignOutLinks))]
    public async Task BrokenSignInOrSignOutLink_ShouldTellAProgramWhyItWasRefused(string link, string rejectedParameter)
    {
        // Arrange: curl and fetch send */*
        using HttpClient client = CreateClientWithoutRedirects();

        // Act
        using HttpResponseMessage response = await GetAsync(client, link, "*/*");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        string body = await response.Content.ReadAsStringAsync();
        body.ShouldNotContain(CallerState);
        using JsonDocument json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("error").GetString().ShouldBe("invalid_request");
        json.RootElement.GetProperty("error_description").GetString().ShouldNotBeNull().ShouldContain($"'{rejectedParameter}'");
        json.RootElement.GetProperty("error_uri").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData(null)]
    public async Task BrokenSignInLink_ShouldTellAProgramWhyItWasRefused_WhateverJsonItAccepts(string? accept)
    {
        // Arrange
        using HttpClient client = CreateClientWithoutRedirects();

        // Act
        using HttpResponseMessage response = await GetAsync(
            client, AuthorizeLink(clientId: "no-such-client", ValidCallback), accept);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("error").GetString().ShouldBe("invalid_request");
    }

    /// <summary>
    /// An <c>Accept</c> that names neither JSON nor HTML goes to the fallback writer, which knows only the
    /// status (#912 follow-up). Nothing sends these to a sign-in link, but if something did, it must still
    /// get problem details and not a page or a 500.
    /// </summary>
    [Theory]
    [InlineData("application/vnd.dev-lotrokoniecdev.hateoas.json")]
    [InlineData("text/plain")]
    public async Task BrokenSignInLink_ShouldAnswerAnyOtherMediaTypeWithProblemDetails(string accept)
    {
        // Arrange
        using HttpClient client = CreateClientWithoutRedirects();

        // Act
        using HttpResponseMessage response = await GetAsync(
            client, AuthorizeLink(clientId: "no-such-client", ValidCallback), accept);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("status").GetInt32().ShouldBe((int)HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// The switch behind #912 covers the browser endpoints only. The token endpoint answers programs, and
    /// OAuth fixes the JSON it returns, so even a request that names <c>text/html</c> gets that JSON.
    /// </summary>
    [Fact]
    public async Task RefusedTokenRequest_ShouldKeepItsOAuthJson_EvenForABrowserAccept()
    {
        // Arrange
        using HttpClient client = CreateClientWithoutRedirects();
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri("/connect/token", UriKind.Relative));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "no-such-client",
            ["client_secret"] = "NotASecret1!"
        });
        request.Headers.Accept.ParseAdd(BrowserAccept);

        // Act
        using HttpResponseMessage response = await client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("error").GetString().ShouldBe("invalid_client");
    }

    private static string AuthorizeLink(string? clientId, string redirectUri, string? codeChallenge = PkceChallenge)
    {
        Dictionary<string, string?> query = new()
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = "openid",
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = codeChallenge is null ? null : "S256",
            ["state"] = CallerState
        };

        return "/connect/authorize" + QueryString.Create(query.Where(parameter => parameter.Value is not null));
    }

    private static string LogoutLink(string postLogoutRedirectUri) =>
        "/connect/logout" + QueryString.Create(new Dictionary<string, string?>
        {
            ["post_logout_redirect_uri"] = postLogoutRedirectUri,
            ["state"] = CallerState
        });

    private HttpClient CreateClientWithoutRedirects() =>
        Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string? accept)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(path, UriKind.Relative));
        if (accept is not null)
        {
            request.Headers.Accept.ParseAdd(accept);
        }

        return await client.SendAsync(request);
    }

    private static async Task<string> LoadLoginFormTokenAsync(HttpClient browser)
    {
        using HttpResponseMessage page = await browser.GetAsync(new Uri(LoginPage, UriKind.Relative));
        page.StatusCode.ShouldBe(HttpStatusCode.OK);

        Match match = AntiforgeryTokenRegex().Match(await page.Content.ReadAsStringAsync());
        match.Success.ShouldBeTrue();
        return match.Groups[1].Value;
    }

    private async Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string? token, string? accept)
    {
        RegisterRequest credentials = UserFactory.GenerateRandomRegisterRequest(Faker);
        Dictionary<string, string> form = new()
        {
            ["Email"] = credentials.Email,
            ["Password"] = credentials.Password
        };
        if (token is not null)
        {
            form["__RequestVerificationToken"] = token;
        }

        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(LoginPage, UriKind.Relative));
        request.Content = new FormUrlEncodedContent(form);
        if (accept is not null)
        {
            request.Headers.Accept.ParseAdd(accept);
        }

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PostMalformedRegisterAsync(HttpClient client, string? accept)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri("/auth/register", UriKind.Relative));
        request.Content = new StringContent("{", Encoding.UTF8, "application/json");
        if (accept is not null)
        {
            request.Headers.Accept.ParseAdd(accept);
        }

        return await client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> PostRegisterWithoutConsentAsync(HttpClient client, string? accept)
    {
        RegisterRequest registerRequest = new(
            Faker.Random.AlphaNumeric(16),
            Faker.Internet.Email(),
            "TestPass1!",
            AcceptedPrivacyPolicy: false,
            AcceptedDataProcessingConsent: true,
            AcceptedTermsOfService: true);

        using HttpRequestMessage request = new(HttpMethod.Post, new Uri("/auth/register", UriKind.Relative));
        request.Content = JsonContent.Create(registerRequest);
        if (accept is not null)
        {
            request.Headers.Accept.ParseAdd(accept);
        }

        return await client.SendAsync(request);
    }

    private static string? StyleNonce(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Content-Security-Policy", out IEnumerable<string>? policies))
        {
            return null;
        }

        Match match = StyleNonceRegex().Match(string.Join(", ", policies));
        return match.Success ? match.Groups["nonce"].Value : null;
    }

    [GeneratedRegex("""name="__RequestVerificationToken".*?value="([^"]+)""")]
    private static partial Regex AntiforgeryTokenRegex();

    [GeneratedRegex(@"style-src [^;]*'nonce-(?<nonce>[A-Za-z0-9_-]+)'")]
    private static partial Regex StyleNonceRegex();
}
