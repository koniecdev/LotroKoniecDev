using System.Net;
using LotroKoniecDev.AuthSystem.Contracts.Features.Admin;
using LotroKoniecDev.Frontend.Tests.Integration.Shared;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.TranslationSystem.Contracts.Discovery;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using AuthDiscoveryResponse = LotroKoniecDev.AuthSystem.Contracts.Discovery.DiscoveryResponse;
using AuthRels = LotroKoniecDev.AuthSystem.Contracts.Hateoas.Rels;

namespace LotroKoniecDev.Frontend.Tests.Integration.Tests.AccountAdministration;

/// <summary>
/// The admin's role forms on the real Production host (#1488): a change redirects, and a post for a form
/// the fresh list no longer draws (a reload, a double click, a stale tab) shows the list instead of
/// Blazor's blank 400 (#1122). The auth API is a stub that keeps the role between calls.
/// </summary>
public sealed class AccountAdministrationFormPostTests : IClassFixture<ProductionFrontendFactory>
{
    private const string TmsOrigin = "https://tms.staging.invalid";
    private const string AuthOrigin = "https://auth.staging.invalid";
    private const string RowId = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";
    private const string PagePath = "/admin/accounts";

    private readonly ProductionFrontendFactory _factory;

    [Fact]
    public async Task Post_TheGrantForm_GivesTheRoleOnceThroughTheAdvertisedHrefAndRedirects()
    {
        // Arrange
        StatefulAuthSystem authSystem = new();
        using WebApplicationFactory<Program> host = CreateHost(authSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(FormPost($"grant-reviewer-{RowId}", antiforgeryToken));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        posted.Headers.Location.ShouldNotBeNull().OriginalString.ShouldEndWith("/admin/accounts?changed=granted");
        authSystem.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task Post_TheGrantFormAgainAfterItSucceeded_ShowsTheFreshListAndWritesNothing()
    {
        // Arrange: the row now offers the removal, so the grant form is gone.
        StatefulAuthSystem authSystem = new();
        using WebApplicationFactory<Program> host = CreateHost(authSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");
        using HttpResponseMessage first = await browser.SendAsync(FormPost($"grant-reviewer-{RowId}", antiforgeryToken));
        first.StatusCode.ShouldBe(HttpStatusCode.Redirect);

        // Act
        using HttpResponseMessage second = await browser.SendAsync(FormPost($"grant-reviewer-{RowId}", antiforgeryToken));

        // Assert
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        string page = WebUtility.HtmlDecode(await second.Content.ReadAsStringAsync());
        page.ShouldContain("<h1>Konta i role</h1>");
        page.ShouldContain("Odbierz rolę Recenzent");
        authSystem.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task Post_TheRemoveFormForARowNoLongerOnThePage_ShowsTheFreshListAndWritesNothing()
    {
        // Arrange
        StatefulAuthSystem authSystem = new() { RowListed = false };
        using WebApplicationFactory<Program> host = CreateHost(authSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(FormPost($"remove-reviewer-{RowId}", antiforgeryToken));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        string page = WebUtility.HtmlDecode(await posted.Content.ReadAsStringAsync());
        page.ShouldContain("<h1>Konta i role</h1>");
        authSystem.Writes.ShouldBe(0);
    }

    public AccountAdministrationFormPostTests(ProductionFrontendFactory factory)
    {
        _factory = factory;
    }

    private WebApplicationFactory<Program> CreateHost(StatefulAuthSystem authSystem) =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.Configure<OpenIdConnectOptions>(
                OpenIdConnectDefaults.AuthenticationScheme,
                options => options.BackchannelHttpHandler = new UnreachableServer());
            services.AddHttpClient("ITranslationSystemClient")
                .ConfigurePrimaryHttpMessageHandler(() => new TmsStub());
            services.AddHttpClient("IAuthSystemClient")
                .ConfigurePrimaryHttpMessageHandler(() => authSystem);
        }));

    private static HttpRequestMessage FormPost(string formName, string antiforgeryToken) =>
        new(HttpMethod.Post, PagePath)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_handler"] = formName,
                ["__RequestVerificationToken"] = antiforgeryToken
            })
        };

    private sealed class TmsStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri ?? throw new InvalidOperationException("A call with no address.");
            return Task.FromResult(uri.GetLeftPart(UriPartial.Path) == $"{TmsOrigin}/"
                ? StubAnswers.HateoasJson(new DiscoveryResponse("tms") { Links = [] })
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    private sealed class StatefulAuthSystem : HttpMessageHandler
    {
        private const string AccountHref = $"{AuthOrigin}/advertised/account";
        private const string AccountsHref = $"{AuthOrigin}/advertised/accounts";
        private const string ReviewerHref = $"{AuthOrigin}/advertised/accounts/{RowId}/reviewer";

        private bool _isReviewer;
        private int _writes;

        /// <summary>How many role changes reached the API.</summary>
        public int Writes => _writes;

        public bool RowListed { get; init; } = true;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri ?? throw new InvalidOperationException("A call with no address.");
            string path = uri.GetLeftPart(UriPartial.Path);

            HttpResponseMessage response = (path, request.Method.Method) switch
            {
                ($"{AuthOrigin}/", "GET") => StubAnswers.HateoasJson(new AuthDiscoveryResponse("auth")
                {
                    Links =
                    [
                        new LinkDto(AccountHref, AuthRels.ExportAccountData, "GET"),
                        new LinkDto(AccountsHref, AuthRels.Accounts, "GET")
                    ]
                }),
                (AccountsHref, "GET") => StubAnswers.HateoasJson(ListResponse()),
                (ReviewerHref, "PUT") => Change(isReviewer: true),
                (ReviewerHref, "DELETE") => Change(isReviewer: false),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };

            return Task.FromResult(response);
        }

        private HttpResponseMessage Change(bool isReviewer)
        {
            Interlocked.Increment(ref _writes);
            _isReviewer = isReviewer;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        private AccountListResponse ListResponse()
        {
            AccountSummaryResponse account = new(
                Guid.Parse(RowId),
                "frodo",
                "frodo@shire.me",
                _isReviewer
                    ? [AuthConstants.Roles.Reviewer, AuthConstants.Roles.Translator]
                    : [AuthConstants.Roles.Translator],
                DeletionScheduledAt: null)
            {
                Links = _isReviewer
                    ? [new LinkDto(ReviewerHref, AuthRels.RemoveReviewer, "DELETE")]
                    : [new LinkDto(ReviewerHref, AuthRels.GrantReviewer, "PUT")]
            };

            AccountSummaryResponse[] items = RowListed ? [account] : [];
            return new AccountListResponse(items, 1, 50, items.Length);
        }
    }
}
