using System.Net;
using LotroKoniecDev.Frontend.Tests.Integration.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.Frontend.Tests.Integration.Tests.Errors;

/// <summary>
/// #1555: the website draws an error page by running the request again on that page's address. For a
/// request that is not a GET, that run must still draw the page, with the status of the first answer.
/// </summary>
public sealed class ErrorPageForNonGetRequestTests : IClassFixture<ProductionFrontendFactory>
{
    private const string ExportPage = "/account/export";
    private const string DownloadPath = "/account/export/download";
    private const string OtherSubject = "22222222-2222-2222-2222-222222222222";
    private const string BadRequestTitle = "<title>Nieprawidłowe żądanie — lotro-translator.pl</title>";
    private const string AccessDeniedTitle = "<title>Brak dostępu — lotro-translator.pl</title>";
    private const string NotFoundTitle = "<title>Nie znaleziono — lotro-translator.pl</title>";
    private const string ErrorTitle = "<title>Błąd — lotro-translator.pl</title>";

    private readonly ProductionFrontendFactory _factory;

    [Fact]
    public async Task Post_TheDataDownloadWithATokenOfAnotherAccount_ShowsTheBadRequestPage()
    {
        // Arrange: the form comes from one account, and another account signed in later in a second tab.
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(_factory);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync(ExportPage);
        browser.SetCookie(SignedInSession.CookieName, SignedInSession.ProtectedTicket(_factory, OtherSubject));

        // Act
        using HttpResponseMessage response = await browser.SendAsync(DownloadPost(antiforgeryToken));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain(BadRequestTitle);
    }

    /// <summary>A key ring that changed or a deleted antiforgery cookie leaves the post with no token.</summary>
    [Fact]
    public async Task Post_TheDataDownloadWithNoToken_ShowsTheBadRequestPage()
    {
        // Arrange
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(_factory);

        // Act
        using HttpResponseMessage response = await browser.SendAsync(DownloadPost(antiforgeryToken: null));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain(BadRequestTitle);
    }

    /// <summary>The request log line is written after the error page and must show the method the browser sent.</summary>
    [Fact]
    public async Task Post_TheDataDownloadWithNoToken_LeavesTheMethodAsPostAfterTheErrorPage()
    {
        // Arrange
        MethodAfterPipelineStartupFilter methodAfterPipeline = new();
        using WebApplicationFactory<Program> host = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IStartupFilter>(methodAfterPipeline)));
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);

        // Act: the body ends only after the whole pipeline has returned.
        using HttpResponseMessage response = await browser.SendAsync(DownloadPost(antiforgeryToken: null));
        await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        methodAfterPipeline.Method.ShouldBe(HttpMethods.Post);
    }

    /// <summary>
    /// The cookie sign-in answers a request marked as Ajax with a 401 and no page, so the error page draws it.
    /// The login check runs before the token check, so no failed token check is stored here.
    /// </summary>
    [Fact]
    public async Task Post_TheDataDownloadAsAnAjaxRequestWithNoSession_ShowsTheAccessDeniedPageWithStatus401()
    {
        // Arrange
        using CookieJarBrowser browser = new(_factory);
        HttpRequestMessage request = DownloadPost(antiforgeryToken: null);
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        // Act
        using HttpResponseMessage response = await browser.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).ShouldContain(AccessDeniedTitle);
    }

    /// <summary>The error pages take only GET and POST, so every other method must reach them as GET.</summary>
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("OPTIONS")]
    public async Task Send_ToAnUnknownAddressWithAMethodOtherThanGet_ShowsTheNotFoundPage(string method)
    {
        // Arrange
        using CookieJarBrowser browser = new(_factory);

        // Act
        using HttpResponseMessage response = await browser.SendAsync(
            new HttpRequestMessage(new HttpMethod(method), "/no-such-page"));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).ShouldContain(NotFoundTitle);
    }

    /// <summary>The login route takes only GET, so a post to it gets no token check before the 405.</summary>
    [Theory]
    [InlineData("PUT", "/dashboard")]
    [InlineData("DELETE", "/")]
    [InlineData("POST", "/auth/login")]
    public async Task Send_ToAnAddressThatDoesNotTakeTheMethod_ShowsTheErrorPageWithStatus405(string method, string path)
    {
        // Arrange
        using CookieJarBrowser browser = new(_factory);

        // Act
        using HttpResponseMessage response = await browser.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        (await response.Content.ReadAsStringAsync()).ShouldContain(ErrorTitle);
    }

    public ErrorPageForNonGetRequestTests(ProductionFrontendFactory factory)
    {
        _factory = factory;
    }

    private static HttpRequestMessage DownloadPost(string? antiforgeryToken)
    {
        Dictionary<string, string> form = new() { ["password"] = "any-password" };
        if (antiforgeryToken is not null)
        {
            form["__RequestVerificationToken"] = antiforgeryToken;
        }

        return new HttpRequestMessage(HttpMethod.Post, DownloadPath) { Content = new FormUrlEncodedContent(form) };
    }

    /// <summary>Stands first in the pipeline, where the request log reads the method.</summary>
    private sealed class MethodAfterPipelineStartupFilter : IStartupFilter
    {
        public string? Method { get; private set; }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    await nextMiddleware(context);
                    Method = context.Request.Method;
                });
                next(app);
            };
        }
    }
}
