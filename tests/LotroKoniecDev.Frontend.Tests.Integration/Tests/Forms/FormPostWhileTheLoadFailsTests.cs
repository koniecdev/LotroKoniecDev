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
/// #1122: a page draws its form only when its data loaded. Blazor sends a post only to a form that is on
/// the page, and outside Development it answers a post to a missing form with an empty 400, which the
/// user sees as a blank page. These tests send each page's real form post, with antiforgery, while the API
/// the page loads from is down, and check that the user gets the page with its Polish error instead.
/// bUnit cannot show this: it calls a form's handler directly and never looks the form up by name.
/// Each test reads its antiforgery token from the sign-out form, which every page has for a signed-in user.
/// </summary>
public sealed partial class FormPostWhileTheLoadFailsTests : IClassFixture<StagingFrontendFactory>
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

    private readonly StagingFrontendFactory _factory;

    [Fact]
    public async Task Post_ImportForm_WhenTheVersionListFailsToLoad_ShowsTheImportPageWithTheLoadError()
    {
        // Arrange: the case the ticket was found with. The admin sends exported.txt, and the list of
        // versions the page loads first cannot be read in that request.
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
        // Arrange: an account page that cannot load because the token was refused sends the user to the
        // login, as it does on a plain visit. Blazor's 400 used to replace that redirect on a post.
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
        // Arrange: the editor's recovery path. The save does not need the row, because the upsert link
        // comes from the service document, so the translator's text is saved and not lost.
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
        // Arrange: only the page's own form names are kept. A post to a name the page never drew is a
        // broken request and keeps Blazor's own answer.
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

    public FormPostWhileTheLoadFailsTests(StagingFrontendFactory factory)
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
    /// The translation API while it is down for page data: the service document still answers, so the
    /// pages find their links, and every other read answers 503. The upsert link answers as the test sets.
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
