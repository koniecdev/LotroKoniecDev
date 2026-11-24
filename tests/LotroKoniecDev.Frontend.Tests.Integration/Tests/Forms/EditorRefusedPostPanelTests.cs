using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using LotroKoniecDev.Frontend.Tests.Integration.Shared;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Contracts.Discovery;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using AuthDiscoveryResponse = LotroKoniecDev.AuthSystem.Contracts.Discovery.DiscoveryResponse;

namespace LotroKoniecDev.Frontend.Tests.Integration.Tests.Forms;

/// <summary>
/// The editor's answer to a save or an approve the translation API refuses still shows the panel of rows
/// with the same English (#1482). The answer to a post is a page of its own, so it must load the panel too.
/// </summary>
public sealed partial class EditorRefusedPostPanelTests : IClassFixture<ProductionFrontendFactory>
{
    private const string TmsOrigin = "https://tms.staging.invalid";
    private const string AuthOrigin = "https://auth.staging.invalid";
    private const string RowId = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";
    private const string OtherRowId = "3f2504e0-4f89-41d3-9a0c-0305e82c3302";
    private const int FileId = 620756992;
    private const long GossipId = 1002;
    private const string StoredPolish = "Topór wojenny";
    private const string TypedPolish = "Krzepki topór";
    private const string OtherRowPolish = "Krzepki Topornik";
    private const string PanelTitle = "Inne wiersze z tym samym tekstem";

    /// <summary>The names ASP.NET Core gives the typed clients, which are internal to the Frontend.</summary>
    private const string TranslationSystemClientName = "ITranslationSystemClient";
    private const string AuthSystemClientName = "IAuthSystemClient";

    private static readonly Uri UpsertHref = new($"{TmsOrigin}/advertised/upsert");
    private static readonly Uri RowHref = new($"{TmsOrigin}/advertised/translations/{RowId}");
    private static readonly Uri ApproveHref = new($"{TmsOrigin}/advertised/translations/{RowId}/approve");
    private static readonly Uri SameSourceTextHref = new($"{TmsOrigin}/advertised/translations/{RowId}/same-source-text");

    private readonly ProductionFrontendFactory _factory;

    [Fact]
    public async Task Post_SaveForm_WhenTheApiRefusesAnEmptyText_ShowsThePanelBesideTheErrorAndTheTypedText()
    {
        // Arrange
        using WebApplicationFactory<Program> host = CreateHost(new StubTranslationSystem());
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(SavePost(antiforgeryToken, draft: string.Empty));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        string page = WebUtility.HtmlDecode(await posted.Content.ReadAsStringAsync());
        page.ShouldContain("Tłumaczenie nie może być puste i nie może przekraczać dozwolonej długości.");
        Match textarea = TranslatedTextarea().Match(page);
        textarea.Success.ShouldBeTrue();
        textarea.Groups["text"].Value.ShouldBe(string.Empty);
        page.ShouldContain(PanelTitle);
        page.ShouldContain(OtherRowPolish);
    }

    [Fact]
    public async Task Post_ApproveForm_WhenTheApiRefusesTheApprove_ShowsThePanelBesideTheError()
    {
        // Arrange
        using WebApplicationFactory<Program> host = CreateHost(new StubTranslationSystem());
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(ApprovePost(antiforgeryToken));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        string page = WebUtility.HtmlDecode(await posted.Content.ReadAsStringAsync());
        page.ShouldContain("Dane zostały w międzyczasie zmienione przez kogoś innego.");
        page.ShouldContain(PanelTitle);
        page.ShouldContain(OtherRowPolish);
    }

    [Fact]
    public async Task Post_SaveForm_WhenThePanelFailsAndTheSaveIsGood_SavesAndRedirects()
    {
        // Arrange: the panel read comes before the write, so its failure must not stop the save.
        StubTranslationSystem translationSystem = new()
        {
            PanelAnswer = HttpStatusCode.ServiceUnavailable,
            SaveSucceeds = true
        };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(SavePost(antiforgeryToken, draft: TypedPolish));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        posted.Headers.Location.ShouldNotBeNull().OriginalString.ShouldEndWith($"/editor/{RowId}?saved=true");
    }

    [Fact]
    public async Task Post_SaveForm_WhenThePanelFailsAndTheApiRefusesTheSave_ShowsThePanelNoticeBesideTheErrorAndTheTypedText()
    {
        // Arrange
        StubTranslationSystem translationSystem = new() { PanelAnswer = HttpStatusCode.ServiceUnavailable };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(SavePost(antiforgeryToken, draft: string.Empty));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        string page = WebUtility.HtmlDecode(await posted.Content.ReadAsStringAsync());
        page.ShouldContain("Tłumaczenie nie może być puste i nie może przekraczać dozwolonej długości.");
        page.ShouldContain("Nie udało się wczytać innych wierszy z tym samym tekstem angielskim.");
        Match textarea = TranslatedTextarea().Match(page);
        textarea.Success.ShouldBeTrue();
        textarea.Groups["text"].Value.ShouldBe(string.Empty);
        page.ShouldNotContain(PanelTitle);
    }

    public EditorRefusedPostPanelTests(ProductionFrontendFactory factory)
    {
        _factory = factory;
    }

    [GeneratedRegex("""<textarea id="translated"[^>]*>(?<text>[^<]*)</textarea>""")]
    private static partial Regex TranslatedTextarea();

    private WebApplicationFactory<Program> CreateHost(StubTranslationSystem translationSystem) =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.Configure<OpenIdConnectOptions>(
                OpenIdConnectDefaults.AuthenticationScheme,
                options => options.BackchannelHttpHandler = new UnreachableServer());
            services.AddHttpClient(TranslationSystemClientName)
                .ConfigurePrimaryHttpMessageHandler(() => translationSystem);
            services.AddHttpClient(AuthSystemClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new StubAuthSystem());
        }));

    private static HttpRequestMessage SavePost(string antiforgeryToken, string draft) =>
        new(HttpMethod.Post, $"/editor/{RowId}")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_handler"] = "save-translation",
                ["__RequestVerificationToken"] = antiforgeryToken,
                ["FileIdField"] = FileId.ToString(CultureInfo.InvariantCulture),
                ["GossipIdField"] = GossipId.ToString(CultureInfo.InvariantCulture),
                ["DraftField"] = draft
            })
        };

    private static HttpRequestMessage ApprovePost(string antiforgeryToken) =>
        new(HttpMethod.Post, $"/editor/{RowId}")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_handler"] = "approve-translation",
                ["__RequestVerificationToken"] = antiforgeryToken
            })
        };

    private static HttpResponseMessage Problem(HttpStatusCode status, string errorCode)
    {
        ProblemDetails problem = new() { Status = (int)status, Title = "Refused" };
        problem.Extensions["errorCode"] = errorCode;
        return new HttpResponseMessage(status)
        {
            Content = JsonContent.Create(problem, mediaType: new MediaTypeHeaderValue("application/problem+json"))
        };
    }

    /// <summary>
    /// The service document, the row and its panel answer. Unless told otherwise, every save is refused as
    /// the API refuses an empty text, and every approve as the API refuses one on a row that changed in the
    /// meantime.
    /// </summary>
    private sealed class StubTranslationSystem : HttpMessageHandler
    {
        public HttpStatusCode PanelAnswer { get; init; } = HttpStatusCode.OK;

        public bool SaveSucceeds { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri ?? throw new InvalidOperationException("A call with no address.");

            HttpResponseMessage response = uri switch
            {
                _ when uri.GetLeftPart(UriPartial.Path) == $"{TmsOrigin}/" => StubAnswers.HateoasJson(
                    new DiscoveryResponse("tms")
                    {
                        Links =
                        [
                            new LinkDto($"{TmsOrigin}/advertised/translations", Rels.Translations, "GET"),
                            new LinkDto(UpsertHref.AbsoluteUri, Rels.Upsert, "PUT")
                        ]
                    }),
                _ when uri == RowHref && request.Method == HttpMethod.Get => StubAnswers.HateoasJson(Row()),
                _ when uri == SameSourceTextHref && request.Method == HttpMethod.Get => PanelAnswer == HttpStatusCode.OK
                    ? StubAnswers.HateoasJson(Panel())
                    : new HttpResponseMessage(PanelAnswer),
                _ when uri == UpsertHref && request.Method == HttpMethod.Put => SaveSucceeds
                    ? StubAnswers.HateoasJson(Row())
                    : Problem(HttpStatusCode.BadRequest, "Translations.Validation"),
                _ when uri == ApproveHref && request.Method == HttpMethod.Post =>
                    Problem(HttpStatusCode.Conflict, "Db.ConcurrencyConflict"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };

            return Task.FromResult(response);
        }

        private static TranslationDetailResponse Row() =>
            new(
                TranslationId.Create(Guid.Parse(RowId)),
                FileId,
                GossipId,
                SourceText: "Stout-axe",
                ArgsOrder: null,
                ArgsId: null,
                TranslatedText: StoredPolish,
                PreviousSourceText: null,
                Submitter: null,
                Approver: null,
                TranslationStatus.Draft,
                CreatedAt: DateTimeOffset.UnixEpoch,
                UpdatedAt: DateTimeOffset.UnixEpoch)
            {
                Links =
                [
                    new LinkDto(UpsertHref.AbsoluteUri, Rels.Upsert, "PUT"),
                    new LinkDto(ApproveHref.AbsoluteUri, Rels.Approve, "POST"),
                    new LinkDto(SameSourceTextHref.AbsoluteUri, Rels.SameSourceText, "GET")
                ]
            };

        private static SameSourceTextTranslationsResponse Panel() =>
            new(
                [
                    new SameSourceTextItemResponse(
                        TranslationId.Create(Guid.Parse(OtherRowId)),
                        FileId,
                        GossipId + 1,
                        OtherRowPolish,
                        TranslationStatus.Draft,
                        new TranslatorSummaryResponse(TranslatorId.Create(), "Gimli"))
                ],
                TotalCount: 1,
                HasConflictingTranslations: true);
    }

    /// <summary>The auth API's service document, with no account links; nothing on this page needs more.</summary>
    private sealed class StubAuthSystem : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri ?? throw new InvalidOperationException("A call with no address.");

            HttpResponseMessage response = uri.GetLeftPart(UriPartial.Path) == $"{AuthOrigin}/"
                ? StubAnswers.HateoasJson(new AuthDiscoveryResponse("auth"))
                : new HttpResponseMessage(HttpStatusCode.NotFound);

            return Task.FromResult(response);
        }
    }
}
