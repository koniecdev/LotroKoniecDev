using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using LotroKoniecDev.Frontend.Tests.Integration.Shared;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Contracts.Discovery;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using AuthDiscoveryResponse = LotroKoniecDev.AuthSystem.Contracts.Discovery.DiscoveryResponse;
using AuthRels = LotroKoniecDev.AuthSystem.Contracts.Hateoas.Rels;

namespace LotroKoniecDev.Frontend.Tests.Integration.Tests.Forms;

/// <summary>
/// #1122: each page's real form post, with antiforgery, sent while the API the page loads from is down,
/// must show the page with its Polish error and never the blank page (see <c>PostedFormTarget</c>).
/// bUnit cannot show this: it calls a form's handler directly and never looks the form up by name.
/// Each test reads its antiforgery token from the sign-out form, which every page has for a signed-in user.
/// </summary>
public sealed partial class FormPostWhileTheLoadFailsTests : IClassFixture<ProductionFrontendFactory>
{
    private const string TmsOrigin = "https://tms.staging.invalid";
    private const string AuthOrigin = "https://auth.staging.invalid";
    private const string RowId = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";

    /// <summary>The Polish text a 503 from an API turns into (ApiProblemCopy).</summary>
    private const string ServiceUnavailableMessage = "Usługa jest chwilowo niedostępna. Spróbuj ponownie za chwilę.";

    /// <summary>The names ASP.NET Core gives the typed clients, which are internal to the Frontend.</summary>
    private const string TranslationSystemClientName = "ITranslationSystemClient";
    private const string AuthSystemClientName = "IAuthSystemClient";

    private static readonly Uri UpsertHref = new($"{TmsOrigin}/advertised/upsert");

    private readonly ProductionFrontendFactory _factory;

    [Fact]
    public async Task Post_ImportForm_WhenTheVersionListFailsToLoad_ShowsTheImportPageWithTheLoadError()
    {
        // Arrange: the admin sends exported.txt, and the version list cannot be read in that request.
        StubTranslationSystem translationSystem = new();
        StubAuthSystem authSystem = new(HttpStatusCode.ServiceUnavailable);
        using WebApplicationFactory<Program> host = CreateHost(translationSystem, authSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(StubAnswers.ImportPost(antiforgeryToken, Guid.NewGuid()));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        string page = WebUtility.HtmlDecode(await posted.Content.ReadAsStringAsync());
        page.ShouldContain("<h1>Import / Eksport</h1>");
        page.ShouldContain(ServiceUnavailableMessage);
        translationSystem.Writes.ShouldBe(0);
    }

    [Theory]
    [InlineData("/game-versions", "register-version", "Wersje gry")]
    [InlineData("/game-versions", $"delete-{RowId}", "Wersje gry")]
    [InlineData("/translations", "bulk-approve", "Tłumaczenia")]
    [InlineData($"/editor/{RowId}", "approve-translation", "Edytor tłumaczenia")]
    [InlineData("/account/change-email", "change-email", "Zmień e-mail")]
    [InlineData("/account/change-password", "change-password", "Zmień hasło")]
    [InlineData("/account/delete", "delete-account", "Usuń konto")]
    public async Task Post_WhenThePageDataFailsToLoad_ShowsThePageWithTheLoadError(
        string path,
        string formName,
        string heading)
    {
        // Arrange
        StubTranslationSystem translationSystem = new();
        StubAuthSystem authSystem = new(HttpStatusCode.ServiceUnavailable);
        using WebApplicationFactory<Program> host = CreateHost(translationSystem, authSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(FormPost(path, formName, antiforgeryToken));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        string page = WebUtility.HtmlDecode(await posted.Content.ReadAsStringAsync());
        page.ShouldContain($"<h1>{heading}</h1>");
        page.ShouldContain(ServiceUnavailableMessage);
        (translationSystem.Writes + authSystem.Writes).ShouldBe(0);
    }

    [Theory]
    [InlineData("/account/change-email", "change-email")]
    [InlineData("/account/change-password", "change-password")]
    [InlineData("/account/delete", "delete-account")]
    public async Task Post_AccountForm_WhenTheAuthApiRefusesTheTokenOnTheLoad_RedirectsToTheLogin(
        string path,
        string formName)
    {
        // Arrange: a refused token sends the user to the login, and Blazor's 400 must not replace that
        // redirect on a post.
        StubTranslationSystem translationSystem = new();
        StubAuthSystem authSystem = new(HttpStatusCode.Unauthorized);
        using WebApplicationFactory<Program> host = CreateHost(translationSystem, authSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(FormPost(path, formName, antiforgeryToken));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Uri redirectedTo = new(new Uri("https://localhost"), posted.Headers.Location.ShouldNotBeNull());
        redirectedTo.AbsolutePath.ShouldBe("/auth/login");
        Uri.UnescapeDataString(redirectedTo.Query).ShouldBe($"?returnUrl={path}");
        authSystem.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task Post_SaveForm_WhenTheRowFailsToLoad_SavesThroughTheServiceDocumentAndRedirects()
    {
        // Arrange: the editor's recovery path. The upsert link comes from the service document, so the
        // save does not need the row.
        StubTranslationSystem translationSystem = new() { UpsertAnswer = HttpStatusCode.OK };
        StubAuthSystem authSystem = new(HttpStatusCode.ServiceUnavailable);
        using WebApplicationFactory<Program> host = CreateHost(translationSystem, authSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(SavePost(antiforgeryToken, "Witaj, wędrowcze."));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        posted.Headers.Location.ShouldNotBeNull().OriginalString.ShouldEndWith($"/editor/{RowId}?saved=true");
        translationSystem.Upserts.ShouldBe([new UpsertTranslationRequest(620756992, 1002, "Witaj, wędrowcze.")]);
    }

    [Fact]
    public async Task Post_SaveForm_WhenTheRowFailsToLoadAndTheSaveIsRefused_KeepsTheTextInTheRecoveryForm()
    {
        // Arrange
        StubTranslationSystem translationSystem = new() { UpsertAnswer = HttpStatusCode.Conflict };
        StubAuthSystem authSystem = new(HttpStatusCode.ServiceUnavailable);
        using WebApplicationFactory<Program> host = CreateHost(translationSystem, authSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(SavePost(antiforgeryToken, "Witaj, wędrowcze."));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        string page = WebUtility.HtmlDecode(await posted.Content.ReadAsStringAsync());
        page.ShouldContain("Operacja jest w konflikcie z aktualnym stanem danych.");
        RecoveryTextarea().Match(page).Groups["text"].Value.ShouldBe("Witaj, wędrowcze.");
        page.ShouldContain("name=\"FileIdField\" value=\"620756992\"");
        page.ShouldContain("name=\"GossipIdField\" value=\"1002\"");
    }

    [Fact]
    public async Task Get_TheRedirectAfterARecoverySave_WhenTheRowStillFailsToLoad_ShowsTheSaveConfirmation()
    {
        // Arrange: the outage often lasts into the redirect, and the page must still say the text was saved.
        StubTranslationSystem translationSystem = new() { UpsertAnswer = HttpStatusCode.OK };
        StubAuthSystem authSystem = new(HttpStatusCode.ServiceUnavailable);
        using WebApplicationFactory<Program> host = CreateHost(translationSystem, authSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");
        using HttpResponseMessage posted = await browser.SendAsync(SavePost(antiforgeryToken, "Witaj, wędrowcze."));
        Uri redirectedTo = posted.Headers.Location
            ?? throw new InvalidOperationException($"The save answered {posted.StatusCode} with no redirect.");

        // Act
        using HttpResponseMessage redirected = await browser.SendAsync(new HttpRequestMessage(HttpMethod.Get, redirectedTo));

        // Assert
        redirected.StatusCode.ShouldBe(HttpStatusCode.OK);
        string page = WebUtility.HtmlDecode(await redirected.Content.ReadAsStringAsync());
        page.ShouldContain("Tłumaczenie zapisano.");
        page.ShouldContain(ServiceUnavailableMessage);
    }

    [Fact]
    public async Task Post_ApproveForm_FromAnAddressWithTheSavedFlag_WhenTheRowFailsToLoad_ShowsNoOldConfirmation()
    {
        // Arrange: the approve posts to the address the save's redirect left, so the old "saved" flag must
        // not read as its answer.
        StubTranslationSystem translationSystem = new();
        StubAuthSystem authSystem = new(HttpStatusCode.ServiceUnavailable);
        using WebApplicationFactory<Program> host = CreateHost(translationSystem, authSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(
            FormPost($"/editor/{RowId}?saved=true", "approve-translation", antiforgeryToken));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        string page = WebUtility.HtmlDecode(await posted.Content.ReadAsStringAsync());
        page.ShouldContain(ServiceUnavailableMessage);
        page.ShouldNotContain("Tłumaczenie zapisano.");
    }

    [Theory]
    [InlineData("/import-export", "no-such-form")]
    [InlineData("/game-versions", "delete-")]
    [InlineData("/game-versions", "delete-not-a-guid")]
    [InlineData($"/editor/{RowId}", "save-translationx")]
    [InlineData("/account/delete", "change-email")]
    public async Task Post_NamingAFormThePageNeverHas_WhileTheLoadFails_StillGetsTheBadRequest(
        string path,
        string formName)
    {
        // Arrange: a post to a name the page never drew is broken and keeps Blazor's own answer.
        StubTranslationSystem translationSystem = new();
        StubAuthSystem authSystem = new(HttpStatusCode.ServiceUnavailable);
        using WebApplicationFactory<Program> host = CreateHost(translationSystem, authSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(FormPost(path, formName, antiforgeryToken));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    public FormPostWhileTheLoadFailsTests(ProductionFrontendFactory factory)
    {
        _factory = factory;
    }

    [GeneratedRegex("""<textarea id="translated-recovery"[^>]*>(?<text>[^<]*)</textarea>""")]
    private static partial Regex RecoveryTextarea();

    private WebApplicationFactory<Program> CreateHost(
        StubTranslationSystem translationSystem,
        StubAuthSystem authSystem) =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.Configure<OpenIdConnectOptions>(
                OpenIdConnectDefaults.AuthenticationScheme,
                options => options.BackchannelHttpHandler = new UnreachableServer());
            services.AddHttpClient(TranslationSystemClientName)
                .ConfigurePrimaryHttpMessageHandler(() => translationSystem);
            services.AddHttpClient(AuthSystemClientName)
                .ConfigurePrimaryHttpMessageHandler(() => authSystem);
        }));

    private static HttpRequestMessage FormPost(string path, string formName, string antiforgeryToken) =>
        new(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_handler"] = formName,
                ["__RequestVerificationToken"] = antiforgeryToken
            })
        };

    private static HttpRequestMessage SavePost(string antiforgeryToken, string draft) =>
        new(HttpMethod.Post, $"/editor/{RowId}")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_handler"] = "save-translation",
                ["__RequestVerificationToken"] = antiforgeryToken,
                ["FileIdField"] = "620756992",
                ["GossipIdField"] = "1002",
                ["DraftField"] = draft
            })
        };

    /// <summary>
    /// The service document still answers, every other read answers 503, and the upsert answers as the test
    /// sets.
    /// </summary>
    private sealed class StubTranslationSystem : HttpMessageHandler
    {
        private readonly ConcurrentQueue<UpsertTranslationRequest> _upserts = new();
        private int _writes;

        public HttpStatusCode UpsertAnswer { get; init; } = HttpStatusCode.ServiceUnavailable;

        /// <summary>How many requests other than a read reached the API, the upserts included.</summary>
        public int Writes => _writes;

        public IReadOnlyList<UpsertTranslationRequest> Upserts => [.. _upserts];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri ?? throw new InvalidOperationException("A call with no address.");
            if (request.Method != HttpMethod.Get)
            {
                Interlocked.Increment(ref _writes);
            }

            if (uri.GetLeftPart(UriPartial.Path) == $"{TmsOrigin}/")
            {
                return StubAnswers.HateoasJson(new DiscoveryResponse("tms")
                {
                    Links =
                    [
                        new LinkDto($"{TmsOrigin}/advertised/translations", Rels.Translations, "GET"),
                        new LinkDto($"{TmsOrigin}/advertised/game-versions", Rels.GameVersions, "GET"),
                        new LinkDto(UpsertHref.AbsoluteUri, Rels.Upsert, "PUT"),
                        new LinkDto($"{TmsOrigin}/advertised/contributions", Rels.ContributionDataExport, "GET")
                    ]
                });
            }

            if (uri == UpsertHref && request.Method == HttpMethod.Put)
            {
                return await UpsertAsync(request, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }

        private async Task<HttpResponseMessage> UpsertAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            UpsertTranslationRequest upsert = await request.Content.ShouldNotBeNull()
                .ReadFromJsonAsync<UpsertTranslationRequest>(cancellationToken)
                ?? throw new InvalidOperationException("An upsert with no body.");
            _upserts.Enqueue(upsert);

            return UpsertAnswer == HttpStatusCode.OK
                ? StubAnswers.HateoasJson(new TranslationDetailResponse(
                    TranslationId.Create(Guid.Parse(RowId)),
                    upsert.FileId,
                    upsert.GossipId,
                    SourceText: "Hello, wanderer.",
                    ArgsOrder: null,
                    ArgsId: null,
                    TranslatedText: upsert.TranslatedText,
                    PreviousSourceText: null,
                    Submitter: null,
                    Approver: null,
                    TranslationStatus.Draft,
                    CreatedAt: DateTimeOffset.UnixEpoch,
                    UpdatedAt: DateTimeOffset.UnixEpoch))
                : new HttpResponseMessage(UpsertAnswer);
        }
    }

    /// <summary>
    /// The auth API: its service document answers with the signed-in links, and the account the account
    /// pages load answers with the status the test sets.
    /// </summary>
    private sealed class StubAuthSystem : HttpMessageHandler
    {
        private const string AccountHref = $"{AuthOrigin}/advertised/account";

        private readonly HttpStatusCode _accountAnswer;
        private int _writes;

        /// <summary>How many requests other than a read reached the API.</summary>
        public int Writes => _writes;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri ?? throw new InvalidOperationException("A call with no address.");
            if (request.Method != HttpMethod.Get)
            {
                Interlocked.Increment(ref _writes);
            }

            HttpResponseMessage response = uri.GetLeftPart(UriPartial.Path) switch
            {
                $"{AuthOrigin}/" => StubAnswers.HateoasJson(new AuthDiscoveryResponse("auth")
                {
                    Links = [new LinkDto(AccountHref, AuthRels.ExportAccountData, "GET")]
                }),
                AccountHref => new HttpResponseMessage(_accountAnswer),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };

            return Task.FromResult(response);
        }

        public StubAuthSystem(HttpStatusCode accountAnswer)
        {
            _accountAnswer = accountAnswer;
        }
    }
}
