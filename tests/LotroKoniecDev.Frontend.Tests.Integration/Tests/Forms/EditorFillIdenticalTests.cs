using System.Collections.Concurrent;
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
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using AuthDiscoveryResponse = LotroKoniecDev.AuthSystem.Contracts.Discovery.DiscoveryResponse;

namespace LotroKoniecDev.Frontend.Tests.Integration.Tests.Forms;

/// <summary>
/// The editor's „Zapisz we wszystkich identycznych (N)” (#1587, spec 0019) on a posted form, which bUnit
/// cannot bind: the slip stop, „mimo to”, the href read from the panel, the refusals and the lines after the
/// redirect.
/// </summary>
public sealed partial class EditorFillIdenticalTests : IClassFixture<ProductionFrontendFactory>
{
    private const string TmsOrigin = "https://tms.staging.invalid";
    private const string AuthOrigin = "https://auth.staging.invalid";
    private const string RowId = "3f2504e0-4f89-41d3-9a0c-0305e82c3402";
    private const int FileId = 620756997;
    private const long GossipId = 2001;
    private const string SourceText = "Hello.";
    private const string StoredText = "Cześć.";
    private const string CleanText = "Witaj.";

    /// <summary>Two spaces in a row, which the English does not have: one slip, with this message.</summary>
    private const string SlipText = "Witaj  świecie.";

    private const string SlipMessage = "W tłumaczeniu są co najmniej dwie spacje z rzędu, a w angielskim nie ma.";
    private const string StopLine = "Tekst nie jest jeszcze zapisany. Popraw go albo zapisz mimo to.";
    private const string NotSavedLine = "Tekst nie jest jeszcze zapisany.";
    private const string NoEmptyRowLine =
        "Nie ma już pustych wierszy z tym samym tekstem angielskim. Ten wiersz zapiszesz przyciskiem „Zapisz”.";
    private const string UnknownLine = "Nie wiadomo, czy tekst zapisano. Panel niżej pokazuje wiersze po tej próbie.";
    private const string ListState = "page=2&search=spear&pageSize=25";
    private const string EditorAddress = $"/editor/{RowId}?{ListState}";

    /// <summary>The names ASP.NET Core gives the typed clients, which are internal to the Frontend.</summary>
    private const string TranslationSystemClientName = "ITranslationSystemClient";
    private const string AuthSystemClientName = "IAuthSystemClient";

    private static readonly Uri UpsertHref = new($"{TmsOrigin}/advertised/upsert");
    private static readonly Uri RowHref = new($"{TmsOrigin}/advertised/translations/{RowId}");
    private static readonly Uri CollectionHref = new($"{TmsOrigin}/advertised/translations");
    private static readonly Uri PanelHref = new($"{TmsOrigin}/advertised/translations/{RowId}/twins");
    private static readonly Uri FillHref = new($"{TmsOrigin}/advertised/translations/{RowId}/fill-everywhere");

    private readonly ProductionFrontendFactory _factory;

    [Fact]
    public async Task Get_WithTheFillLinkOnThePanel_ShowsTheButtonWithThePanelsCount()
    {
        // Arrange
        StubTranslationSystem translationSystem = new() { EmptyRows = 1700 };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);

        // Act
        string page = await ReadPageAsync(browser, EditorAddress);

        // Assert
        FillButton().Match(page).Groups["label"].Value.ShouldBe("Zapisz we wszystkich identycznych (1700)");
        page.ShouldNotContain("Zapisz we wszystkich mimo to");
    }

    [Fact]
    public async Task Get_WithoutTheFillLink_ShowsNoFillButton()
    {
        // Arrange: no empty row is left, so the API sends no link.
        StubTranslationSystem translationSystem = new() { EmptyRows = 0 };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);

        // Act
        string page = await ReadPageAsync(browser, EditorAddress);

        // Assert
        FillButton().IsMatch(page).ShouldBeFalse();
    }

    [Fact]
    public async Task Post_Fill_WithNoSlips_PostsToThePanelsHrefOnceAndRedirectsWithTheCount()
    {
        // Arrange
        StubTranslationSystem translationSystem = new() { EmptyRows = 3, FilledCount = 3, TranslationSaved = true };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(FillPost(antiforgeryToken, CleanText, "fill"));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        posted.Headers.Location.ShouldNotBeNull().OriginalString.ShouldEndWith(
            $"/editor/{RowId}?{ListState}&saved=true&filled=3");
        translationSystem.Fills.ShouldBe([new FillIdenticalTranslationsRequest(CleanText)]);
        translationSystem.Upserts.ShouldBeEmpty();
        translationSystem.PanelReads.ShouldBe(1);
    }

    [Fact]
    public async Task Post_Fill_WhenThisRowWasNotSaved_RedirectsWithTheCountOnly()
    {
        // Arrange
        StubTranslationSystem translationSystem = new() { EmptyRows = 2, FilledCount = 2, TranslationSaved = false };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(FillPost(antiforgeryToken, StoredText, "fill"));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        posted.Headers.Location.ShouldNotBeNull().OriginalString.ShouldEndWith($"/editor/{RowId}?{ListState}&filled=2");
    }

    [Fact]
    public async Task Post_Fill_WithSlips_SendsNothingAndShowsTheStopAboveTheSlipsDrawnOnce()
    {
        // Arrange: the address still carries an old confirmation, which a stop must not show.
        StubTranslationSystem translationSystem = new() { EmptyRows = 3 };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(
            FillPost(antiforgeryToken, SlipText, "fill", address: EditorAddress + "&saved=true&filled=3"));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        string page = WebUtility.HtmlDecode(await posted.Content.ReadAsStringAsync());
        translationSystem.Fills.ShouldBeEmpty();
        translationSystem.Upserts.ShouldBeEmpty();
        page.IndexOf(StopLine, StringComparison.Ordinal).ShouldBeLessThan(page.IndexOf(SlipMessage, StringComparison.Ordinal));
        Regex.Count(page, Regex.Escape(SlipMessage)).ShouldBe(1);
        FillAnywayButton().IsMatch(page).ShouldBeTrue();
        FillButton().IsMatch(page).ShouldBeTrue();
        TranslatedTextarea().Match(page).Groups["text"].Value.ShouldBe(SlipText);
        page.ShouldNotContain("Tłumaczenie zapisano.");
        page.ShouldNotContain("Ten sam tekst wpisano");
        translationSystem.PanelReads.ShouldBe(1);
    }

    [Fact]
    public async Task Post_FillAnyway_WithSlips_FillsTheTextAsItStands()
    {
        // Arrange
        StubTranslationSystem translationSystem = new() { EmptyRows = 3, FilledCount = 3, TranslationSaved = true };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(FillPost(antiforgeryToken, SlipText, "fill-anyway"));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        translationSystem.Fills.ShouldBe([new FillIdenticalTranslationsRequest(SlipText)]);
    }

    [Fact]
    public async Task Post_Fill_WithSlipsWhenThePanelHasNoLinkAnyMore_SaysNoEmptyRowIsLeftAndOffersNoMimoTo()
    {
        // Arrange
        StubTranslationSystem translationSystem = new() { EmptyRows = 0 };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(FillPost(antiforgeryToken, SlipText, "fill"));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        string page = WebUtility.HtmlDecode(await posted.Content.ReadAsStringAsync());
        page.ShouldContain(NotSavedLine);
        page.ShouldContain(NoEmptyRowLine);
        page.ShouldNotContain(StopLine);
        FillAnywayButton().IsMatch(page).ShouldBeFalse();
    }

    [Fact]
    public async Task Post_Fill_WhenThePanelHasNoLinkAnyMore_SavesNothingAndSaysNoEmptyRowIsLeft()
    {
        // Arrange: someone filled the last empty row after the page was loaded. The plain save would turn an
        // approved row into a draft, so nothing falls back to it.
        StubTranslationSystem translationSystem = new() { EmptyRows = 0 };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(FillPost(antiforgeryToken, CleanText, "fill"));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        string page = WebUtility.HtmlDecode(await posted.Content.ReadAsStringAsync());
        translationSystem.Fills.ShouldBeEmpty();
        translationSystem.Upserts.ShouldBeEmpty();
        page.ShouldContain(NotSavedLine);
        page.ShouldContain(NoEmptyRowLine);
        TranslatedTextarea().Match(page).Groups["text"].Value.ShouldBe(CleanText);
        translationSystem.PanelReads.ShouldBe(1);
    }

    [Fact]
    public async Task Post_Fill_WhenThePanelFails_SavesNothingAndShowsThePanelsLine()
    {
        // Arrange
        StubTranslationSystem translationSystem = new() { EmptyRows = 3, PanelFails = true };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(FillPost(antiforgeryToken, CleanText, "fill"));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        string page = WebUtility.HtmlDecode(await posted.Content.ReadAsStringAsync());
        translationSystem.Fills.ShouldBeEmpty();
        translationSystem.Upserts.ShouldBeEmpty();
        page.ShouldContain(NotSavedLine);
        page.ShouldContain("Nie udało się wczytać innych wierszy z tym samym tekstem angielskim.");
        page.ShouldNotContain(NoEmptyRowLine);
    }

    [Fact]
    public async Task Post_Fill_WhenTheRowDoesNotLoad_SavesNothingAndKeepsTheTextInTheRecoveryForm()
    {
        // Arrange
        StubTranslationSystem translationSystem = new() { EmptyRows = 3, RowLoads = false };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(FillPost(antiforgeryToken, CleanText, "fill"));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        string page = WebUtility.HtmlDecode(await posted.Content.ReadAsStringAsync());
        translationSystem.Fills.ShouldBeEmpty();
        translationSystem.Upserts.ShouldBeEmpty();
        page.ShouldContain(NotSavedLine);
        page.ShouldContain("„Zapisz ponownie” zapisze tylko ten wiersz.");
        RecoveryTextarea().Match(page).Groups["text"].Value.ShouldBe(CleanText);
        FillButton().IsMatch(page).ShouldBeFalse();
    }

    [Theory]
    [InlineData(1, "Kopia do 1 wiersza nie mieści się w dziennym limicie zapisów.")]
    [InlineData(1700, "Kopia do 1700 wierszy nie mieści się w dziennym limicie zapisów.")]
    public async Task Post_Fill_RefusedByTheSaveLimit_ShowsTheCopySentenceAndKeepsTheText(int emptyRows, string expected)
    {
        // Arrange
        StubTranslationSystem translationSystem = new()
        {
            EmptyRows = emptyRows,
            FillAnswer = HttpStatusCode.TooManyRequests,
            FillErrorCode = "Translations.SaveBudgetExhausted"
        };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(FillPost(antiforgeryToken, CleanText, "fill"));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        string page = WebUtility.HtmlDecode(await posted.Content.ReadAsStringAsync());
        page.ShouldContain(expected + " Sam ten wiersz możesz zapisać przyciskiem „Zapisz”.");
        TranslatedTextarea().Match(page).Groups["text"].Value.ShouldBe(CleanText);
        page.ShouldNotContain(UnknownLine);
    }

    [Fact]
    public async Task Post_Fill_RefusedByTheOverwriteLimit_ShowsItsUsualSentence()
    {
        // Arrange
        StubTranslationSystem translationSystem = new()
        {
            EmptyRows = 3,
            FillAnswer = HttpStatusCode.TooManyRequests,
            FillErrorCode = "Translations.OverwriteBudgetExhausted"
        };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(FillPost(antiforgeryToken, CleanText, "fill"));

        // Assert
        string page = WebUtility.HtmlDecode(await posted.Content.ReadAsStringAsync());
        page.ShouldContain("Wykorzystano limit poprawek istniejących tłumaczeń.");
        page.ShouldNotContain("Kopia do");
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    public async Task Post_Fill_ThatFails_SaysTheOutcomeIsUnknownOnlyAfterAServerError(HttpStatusCode answer, bool unknown)
    {
        // Arrange: a server error can come after the commit; a refusal comes before any write.
        StubTranslationSystem translationSystem = new() { EmptyRows = 3, FillAnswer = answer, FillErrorCode = "Some.Code" };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(FillPost(antiforgeryToken, CleanText, "fill"));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        string page = WebUtility.HtmlDecode(await posted.Content.ReadAsStringAsync());
        page.Contains(UnknownLine, StringComparison.Ordinal).ShouldBe(unknown);
        translationSystem.PanelReads.ShouldBe(unknown ? 2 : 1);
        translationSystem.Fills.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Post_Fill_WhenTheConnectionFails_SaysNothingAboutAnUnknownOutcome()
    {
        // Arrange: the website made this failure itself.
        StubTranslationSystem translationSystem = new() { EmptyRows = 3, FillThrows = true };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(FillPost(antiforgeryToken, CleanText, "fill"));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        string page = WebUtility.HtmlDecode(await posted.Content.ReadAsStringAsync());
        page.ShouldNotContain(UnknownLine);
        page.ShouldContain("Nie udało się połączyć z serwerem.");
        translationSystem.FillAttempts.ShouldBe(1);
    }

    [Theory]
    [InlineData("saved=true&filled=3", "Tłumaczenie zapisano. Ten sam tekst wpisano też w 3 pustych wierszach z tym samym angielskim tekstem.")]
    [InlineData("saved=true&filled=1", "Tłumaczenie zapisano. Ten sam tekst wpisano też w 1 pustym wierszu z tym samym angielskim tekstem.")]
    [InlineData("filled=2", "Ten sam tekst wpisano w 2 pustych wierszach z tym samym angielskim tekstem. Ten wiersz już go miał, więc się nie zmienił.")]
    [InlineData("saved=true&filled=0", "Tłumaczenie zapisano. Nie było już pustych wierszy z tym samym tekstem angielskim.")]
    [InlineData("filled=0", "Nie było już pustych wierszy z tym samym tekstem angielskim. Ten wiersz już miał ten tekst, więc nic nie zapisano.")]
    public async Task Get_AfterAFill_ShowsOneLineThatNamesTheFilledRows(string flags, string expected)
    {
        // Arrange
        StubTranslationSystem translationSystem = new() { EmptyRows = 0 };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);

        // Act
        string page = await ReadPageAsync(browser, $"{EditorAddress}&{flags}");

        // Assert
        page.ShouldContain(expected);
        Regex.Count(page, "Tłumaczenie zapisano").ShouldBe(expected.StartsWith("Tłumaczenie zapisano", StringComparison.Ordinal) ? 1 : 0);
    }

    [Fact]
    public async Task Get_WithACountThatIsNotANumber_ShowsThePageWithoutTheFillLine()
    {
        // Arrange
        StubTranslationSystem translationSystem = new() { EmptyRows = 0 };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);

        // Act
        string page = await ReadPageAsync(browser, $"{EditorAddress}&saved=true&filled=-1");

        // Assert
        page.ShouldContain("Tłumaczenie zapisano.");
        page.ShouldNotContain("Ten sam tekst wpisano");
    }

    public EditorFillIdenticalTests(ProductionFrontendFactory factory)
    {
        _factory = factory;
    }

    [GeneratedRegex("""<textarea id="translated"[^>]*>\n(?<text>[^<]*)</textarea>""")]
    private static partial Regex TranslatedTextarea();

    [GeneratedRegex("""<textarea id="translated-recovery"[^>]*>(?<text>[^<]*)</textarea>""")]
    private static partial Regex RecoveryTextarea();

    /// <summary>The fill button in any attribute order.</summary>
    [GeneratedRegex("""<button(?=[^>]*\bname="SaveAction")(?=[^>]*\bvalue="fill")[^>]*>(?<label>[^<]*)</button>""")]
    private static partial Regex FillButton();

    [GeneratedRegex("""<button(?=[^>]*\bname="SaveAction")(?=[^>]*\bvalue="fill-anyway")[^>]*>Zapisz we wszystkich mimo to</button>""")]
    private static partial Regex FillAnywayButton();

    private static async Task<string> ReadPageAsync(CookieJarBrowser browser, string address)
    {
        using HttpResponseMessage response = await browser.SendAsync(new HttpRequestMessage(HttpMethod.Get, address));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

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

    private static HttpRequestMessage FillPost(
        string antiforgeryToken,
        string typed,
        string saveAction,
        string address = EditorAddress)
        => new(HttpMethod.Post, address)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_handler"] = "save-translation",
                ["__RequestVerificationToken"] = antiforgeryToken,
                ["FileIdField"] = FileId.ToString(CultureInfo.InvariantCulture),
                ["GossipIdField"] = GossipId.ToString(CultureInfo.InvariantCulture),
                ["DraftField"] = typed,
                ["SaveAction"] = saveAction
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
    /// The service document, the row, its panel with the fill link while empty rows exist, the fill and the
    /// save. The fill answers with the counts it is told to.
    /// </summary>
    private sealed class StubTranslationSystem : HttpMessageHandler
    {
        private readonly ConcurrentQueue<FillIdenticalTranslationsRequest> _fills = new();
        private readonly ConcurrentQueue<UpsertTranslationRequest> _upserts = new();
        private int _panelReads;
        private int _fillAttempts;

        public int EmptyRows { get; init; }

        public bool PanelFails { get; init; }

        public bool RowLoads { get; init; } = true;

        public HttpStatusCode FillAnswer { get; init; } = HttpStatusCode.OK;

        public string? FillErrorCode { get; init; }

        public bool FillThrows { get; init; }

        public int FilledCount { get; init; }

        public bool TranslationSaved { get; init; } = true;

        public IReadOnlyList<FillIdenticalTranslationsRequest> Fills => [.. _fills];

        public IReadOnlyList<UpsertTranslationRequest> Upserts => [.. _upserts];

        public int PanelReads => _panelReads;

        public int FillAttempts => _fillAttempts;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri ?? throw new InvalidOperationException("A call with no address.");

            if (uri.GetLeftPart(UriPartial.Path) == $"{TmsOrigin}/")
            {
                return StubAnswers.HateoasJson(new DiscoveryResponse("tms")
                {
                    Links =
                    [
                        new LinkDto(CollectionHref.AbsoluteUri, Rels.Translations, "GET"),
                        new LinkDto(UpsertHref.AbsoluteUri, Rels.Upsert, "PUT"),

                        // The signed-in marker: without it the website takes the session for dead after this answer.
                        new LinkDto($"{TmsOrigin}/advertised/contributions", Rels.ContributionDataExport, "GET")
                    ]
                });
            }

            if (uri == RowHref && request.Method == HttpMethod.Get)
            {
                return RowLoads
                    ? StubAnswers.HateoasJson(Row(StoredText))
                    : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            if (uri == PanelHref && request.Method == HttpMethod.Get)
            {
                Interlocked.Increment(ref _panelReads);
                return PanelFails
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : StubAnswers.HateoasJson(Panel());
            }

            if (uri == FillHref && request.Method == HttpMethod.Post)
            {
                Interlocked.Increment(ref _fillAttempts);
                if (FillThrows)
                {
                    throw new HttpRequestException("The connection was refused.");
                }

                FillIdenticalTranslationsRequest fill = await request.Content.ShouldNotBeNull()
                    .ReadFromJsonAsync<FillIdenticalTranslationsRequest>(cancellationToken)
                    ?? throw new InvalidOperationException("A fill with no body.");
                _fills.Enqueue(fill);
                return FillAnswer == HttpStatusCode.OK
                    ? StubAnswers.HateoasJson(new FillIdenticalTranslationsResponse(Row(fill.TranslatedText), FilledCount, TranslationSaved))
                    : Problem(FillAnswer, FillErrorCode ?? "Some.Code");
            }

            if (uri == UpsertHref && request.Method == HttpMethod.Put)
            {
                UpsertTranslationRequest upsert = await request.Content.ShouldNotBeNull()
                    .ReadFromJsonAsync<UpsertTranslationRequest>(cancellationToken)
                    ?? throw new InvalidOperationException("An upsert with no body.");
                _upserts.Enqueue(upsert);
                return StubAnswers.HateoasJson(Row(upsert.TranslatedText));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private SameSourceTextTranslationsResponse Panel()
        {
            SameSourceTextItemResponse twin = new(
                TranslationId.Create(Guid.NewGuid()), FileId, GossipId + 1, null, TranslationStatus.Untranslated, null);

            return new SameSourceTextTranslationsResponse([twin], TotalCount: 1, HasConflictingTranslations: false, EmptyRows)
            {
                Links = EmptyRows > 0 ? [new LinkDto(FillHref.AbsoluteUri, Rels.FillIdentical, "POST")] : []
            };
        }

        private static TranslationDetailResponse Row(string? translatedText) =>
            new(
                TranslationId.Create(Guid.Parse(RowId)),
                FileId,
                GossipId,
                SourceText,
                ArgsOrder: null,
                ArgsId: null,
                translatedText,
                PreviousSourceText: null,
                Submitter: null,
                Approver: null,
                translatedText is null ? TranslationStatus.Untranslated : TranslationStatus.Draft,
                CreatedAt: DateTimeOffset.UnixEpoch,
                UpdatedAt: DateTimeOffset.UnixEpoch)
            {
                Links =
                [
                    new LinkDto(UpsertHref.AbsoluteUri, Rels.Upsert, "PUT"),
                    new LinkDto(PanelHref.AbsoluteUri, Rels.SameSourceText, "GET")
                ]
            };
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
