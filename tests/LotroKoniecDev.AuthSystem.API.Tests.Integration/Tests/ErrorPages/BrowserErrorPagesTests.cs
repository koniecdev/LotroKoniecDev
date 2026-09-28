using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.ErrorPages;

/// <summary>
/// A browser that gets a 4xx answer from the auth origin reads a Polish page, and an API client keeps the
/// problem details it can parse (#879). The 5xx page is checked next to the failures that cause it (#867).
/// </summary>
public sealed partial class BrowserErrorPagesTests : EndpointsTestBase
{
    private const string BrowserAccept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
    private const string LoginPage = "/Account/Login";

    public BrowserErrorPagesTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task UnknownAddress_ShouldShowABrowserThePolishNotFoundPage()
    {
        // Arrange
        using HttpClient browser = CreateBrowser();

        // Act
        using HttpResponseMessage response = await GetAsync(browser, "/does-not-exist", BrowserAccept);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");
        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("<h1>Nie ma takiej strony</h1>");
        html.ShouldContain($"<style nonce=\"{StyleNonce(response)}\">");
    }

    [Theory]
    [InlineData("application/vnd.dev-lotrokoniecdev.hateoas.json")]
    [InlineData("application/json")]
    [InlineData("*/*")]
    [InlineData(null)]
    public async Task UnknownAddress_ShouldAnswerAnApiClientWithProblemDetails(string? accept)
    {
        // Arrange: the frontend's back-channel sends the first value, and curl or fetch send */*
        using HttpClient client = CreateBrowser();

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
        using HttpClient formLoader = CreateBrowser();
        string token = await LoadLoginFormTokenAsync(formLoader);
        using HttpClient browserWithoutTheCookie = CreateBrowser();

        // Act
        using HttpResponseMessage response = await PostLoginAsync(browserWithoutTheCookie, token, BrowserAccept);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");
        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("<h1>Formularz wygasł</h1>");
        html.ShouldContain($"<style nonce=\"{StyleNonce(response)}\">");
    }

    [Fact]
    public async Task LoginPost_ShouldShowABrowserTheFormExpiredPage_WhenTheFormCarriesNoToken()
    {
        // Arrange
        using HttpClient browser = CreateBrowser();

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
        using HttpClient client = CreateBrowser();

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
        using HttpClient browser = CreateBrowser();

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
        using HttpClient client = CreateBrowser();

        // Act
        using HttpResponseMessage response = await PostMalformedRegisterAsync(client, accept);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("errorCode").GetString().ShouldBe("Http.BadRequest");
    }

    private HttpClient CreateBrowser() =>
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

    private static string StyleNonce(HttpResponseMessage response)
    {
        string policy = response.Headers.GetValues("Content-Security-Policy").Single();
        Match match = StyleNonceRegex().Match(policy);
        match.Success.ShouldBeTrue();
        return match.Groups["nonce"].Value;
    }

    [GeneratedRegex("""name="__RequestVerificationToken".*?value="([^"]+)""")]
    private static partial Regex AntiforgeryTokenRegex();

    [GeneratedRegex(@"style-src [^;]*'nonce-(?<nonce>[A-Za-z0-9_-]+)'")]
    private static partial Regex StyleNonceRegex();
}
