using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using LotroKoniecDev.Frontend.Tests.Integration.Shared;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
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

namespace LotroKoniecDev.Frontend.Tests.Integration.Tests.Forms;

/// <summary>
/// "Zapisz i następny" (#1490, spec 0018) on a posted form, which bUnit cannot bind: which button was
/// pressed, the text the form showed, the save, the read of the rows after the place and the redirect.
/// </summary>
public sealed partial class EditorSaveAndNextTests : IClassFixture<ProductionFrontendFactory>
{
    private const string TmsOrigin = "https://tms.staging.invalid";
    private const string AuthOrigin = "https://auth.staging.invalid";
    private const string RowId = "3f2504e0-4f89-41d3-9a0c-0305e82c3302";
    private const string NextRowId = "3f2504e0-4f89-41d3-9a0c-0305e82c3303";
    private const int FileId = 620756992;
    private const long GossipId = 1003;

    /// <summary>The English ends with no line break, so the save drops the line breaks at the end of the Polish.</summary>
    private const string SourceText = "Hello.";

    private const string StoredText = "Cześć.";
    private const string Place = "azo2MjA3NTY5OTI6MTAwMw";
    private const string NextPlace = "azo2MjA3NTY5OTI6MTAwNA";

    /// <summary>The list the editor came from, as "Edytuj" writes it, with the row's place last.</summary>
    private const string ListState = "page=2&search=axe&status=Untranslated&pageSize=25";

    private const string EditorAddress = $"/editor/{RowId}?{ListState}&place={Place}";

    /// <summary>The names ASP.NET Core gives the typed clients, which are internal to the Frontend.</summary>
    private const string TranslationSystemClientName = "ITranslationSystemClient";
    private const string AuthSystemClientName = "IAuthSystemClient";

    private static readonly Uri UpsertHref = new($"{TmsOrigin}/advertised/upsert");
    private static readonly Uri ApproveHref = new($"{TmsOrigin}/advertised/translations/{RowId}/approve");
    private static readonly Uri RowHref = new($"{TmsOrigin}/advertised/translations/{RowId}");
    private static readonly Uri CollectionHref = new($"{TmsOrigin}/advertised/translations");

    private readonly ProductionFrontendFactory _factory;

    [Fact]
    public async Task Get_WithAStoredTextThatStartsWithALineBreak_WritesOneLineBreakForTheBrowserToDrop()
    {
        // Arrange
        StubTranslationSystem translationSystem = new() { StoredTranslatedText = "\nX" };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);

        // Act
        using HttpResponseMessage response = await browser.SendAsync(new HttpRequestMessage(HttpMethod.Get, EditorAddress));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string page = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        TranslatedTextarea().Match(page).Groups["text"].Value.ShouldBe("\nX");
        ShownTextField().Match(page).Groups["text"].Value.ShouldBe("\nX");
        page.ShouldContain("Zapisz i następny");
    }

    [Fact]
    public async Task Post_SaveAndNext_WithAChangedText_SavesAndOpensTheNextRowOnThePageTheApiNames()
    {
        // Arrange
        StubTranslationSystem translationSystem = new() { RowsAfter = NextRowOnPage(3) };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(
            SavePost(antiforgeryToken, typed: "Witaj.", shown: StoredText, saveAction: "next"));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        posted.Headers.Location.ShouldNotBeNull().OriginalString.ShouldEndWith(
            $"/editor/{NextRowId}?page=3&search=axe&status=Untranslated&pageSize=25&place={NextPlace}&previousSaved=true");
        translationSystem.Upserts.ShouldBe([new UpsertTranslationRequest(FileId, GossipId, "Witaj.")]);
        translationSystem.ListReads.ShouldBe(
            [$"?search=axe&status=Untranslated&after={Place}&page=2&pageSize=25"]);
    }

    [Theory]
    [InlineData(StoredText, StoredText)]
    [InlineData(StoredText, StoredText + "\r\n\r\n")]
    [InlineData("Pierwsza\r\nDruga.", "Pierwsza\r\nDruga.")]
    [InlineData("\r\nCześć.", "\r\nCześć.")]
    public async Task Post_SaveAndNext_WithTheShownTextUnchanged_SendsNoSaveAndOpensTheNextRowWithNoLine(
        string shown,
        string typed)
    {
        // Arrange: the save would drop the line breaks at the end, so they do not count as a change.
        StubTranslationSystem translationSystem = new() { RowsAfter = NextRowOnPage(2) };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(
            SavePost(antiforgeryToken, typed, shown, saveAction: "next"));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        posted.Headers.Location.ShouldNotBeNull().OriginalString.ShouldEndWith(
            $"/editor/{NextRowId}?page=2&search=axe&status=Untranslated&pageSize=25&place={NextPlace}");
        translationSystem.Upserts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Post_SaveAndNext_WithAnUnchangedTextThatHasSlips_MovesOnWithoutASave()
    {
        // Arrange: the first press saved this text and stayed to show its slips (a space at the end, no full
        // stop). Some slips are right in Polish, so a second press with no change must move on (Q4).
        StubTranslationSystem translationSystem = new() { StoredTranslatedText = "Cześć ", RowsAfter = NextRowOnPage(2) };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(SavePost(
            antiforgeryToken,
            typed: "Cześć ",
            shown: "Cześć ",
            saveAction: "next",
            address: $"{EditorAddress}&saved=true"));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        posted.Headers.Location.ShouldNotBeNull().OriginalString.ShouldEndWith(
            $"/editor/{NextRowId}?page=2&search=axe&status=Untranslated&pageSize=25&place={NextPlace}");
        translationSystem.Upserts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Post_SaveAndNext_WhenSomeoneSavedANewerTextAfterThePageLoaded_LeavesTheNewerText()
    {
        // Arrange: the translator walks on without editing; the row now holds another person's text.
        StubTranslationSystem translationSystem = new() { StoredTranslatedText = "Nowszy tekst.", RowsAfter = NextRowOnPage(2) };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(
            SavePost(antiforgeryToken, typed: StoredText, shown: StoredText, saveAction: "next"));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        translationSystem.Upserts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Post_SaveAndNext_OnAnUntranslatedRowWithAnEmptyField_StillSendsTheSaveAndShowsItsRefusal()
    {
        // Arrange: an empty field on an untranslated row stays a failed save (owner decision of 2026-11-24).
        StubTranslationSystem translationSystem = new()
        {
            StoredTranslatedText = null,
            UpsertAnswer = HttpStatusCode.BadRequest,
            RowsAfter = NextRowOnPage(2)
        };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(
            SavePost(antiforgeryToken, typed: string.Empty, shown: string.Empty, saveAction: "next"));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        translationSystem.Upserts.ShouldBe([new UpsertTranslationRequest(FileId, GossipId, string.Empty)]);
        translationSystem.ListReads.ShouldBeEmpty();
        (await posted.Content.ReadAsStringAsync()).ShouldContain("status-message status-error");
    }

    [Fact]
    public async Task Post_SaveAndNext_WhenTheSaveFails_StaysWithTheTypedTextTheShownTextAndNoOldLine()
    {
        // Arrange: the form posts to its own address, so the last redirect's flags come along.
        StubTranslationSystem translationSystem = new() { UpsertAnswer = HttpStatusCode.Conflict, RowsAfter = NextRowOnPage(2) };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(SavePost(
            antiforgeryToken,
            typed: "Witaj.",
            shown: StoredText,
            saveAction: "next",
            address: $"{EditorAddress}&previousSaved=true&nextFailed=true"));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        string page = WebUtility.HtmlDecode(await posted.Content.ReadAsStringAsync());
        TranslatedTextarea().Match(page).Groups["text"].Value.ShouldBe("Witaj.");
        ShownTextField().Match(page).Groups["text"].Value.ShouldBe(StoredText);
        page.ShouldNotContain("Poprzednie tłumaczenie zapisano.");
        page.ShouldNotContain("Nie udało się otworzyć następnego wiersza.");
        translationSystem.ListReads.ShouldBeEmpty();
    }

    [Fact]
    public async Task Post_SaveAndNext_WhenTheSavedTextHasSlips_StaysOnTheRowWithTheSavedLine()
    {
        // Arrange: a space at the end and no full stop, which the English has.
        StubTranslationSystem translationSystem = new() { RowsAfter = NextRowOnPage(2) };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(
            SavePost(antiforgeryToken, typed: "Cześć ", shown: StoredText, saveAction: "next"));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        posted.Headers.Location.ShouldNotBeNull().OriginalString.ShouldEndWith($"{EditorAddress}&saved=true");
        translationSystem.Upserts.Count.ShouldBe(1);
        translationSystem.ListReads.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Witaj.", "saved")]
    [InlineData(StoredText, "unchanged")]
    public async Task Post_SaveAndNext_AfterTheLastRow_GoesBackToTheListOnThePageTheApiNames(string typed, string note)
    {
        // Arrange
        StubTranslationSystem translationSystem = new() { RowsAfter = NoRowAfterThePlace(lastPage: 3) };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(
            SavePost(antiforgeryToken, typed, shown: StoredText, saveAction: "next"));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        posted.Headers.Location.ShouldNotBeNull().OriginalString.ShouldEndWith(
            $"/translations?page=3&search=axe&status=Untranslated&pageSize=25&lastRow={note}");
    }

    [Theory]
    [InlineData("Witaj.", "&saved=true&nextFailed=true")]
    [InlineData(StoredText, "&nextFailed=true")]
    public async Task Post_SaveAndNext_WhenTheRowsAfterCannotBeRead_StaysOnTheRowWithTheLines(string typed, string flags)
    {
        // Arrange: no RowsAfter, so the list read fails.
        StubTranslationSystem translationSystem = new();
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(
            SavePost(antiforgeryToken, typed, shown: StoredText, saveAction: "next"));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        posted.Headers.Location.ShouldNotBeNull().OriginalString.ShouldEndWith($"{EditorAddress}{flags}");
    }

    [Fact]
    public async Task Post_Save_WithAPlace_StaysOnTheRowAndKeepsThePlace()
    {
        // Arrange: "Zapisz" sends no button value.
        StubTranslationSystem translationSystem = new() { RowsAfter = NextRowOnPage(2) };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(
            SavePost(antiforgeryToken, typed: StoredText, shown: StoredText, saveAction: null));

        // Assert: an unchanged text is saved, as "Zapisz" does today.
        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        posted.Headers.Location.ShouldNotBeNull().OriginalString.ShouldEndWith($"{EditorAddress}&saved=true");
        translationSystem.Upserts.Count.ShouldBe(1);
        translationSystem.ListReads.ShouldBeEmpty();
    }

    [Fact]
    public async Task Post_SaveAndNext_WithoutAPlace_SavesAndStaysAsSaveDoes()
    {
        // Arrange: a hand-made post, since the page draws no button without a place.
        StubTranslationSystem translationSystem = new() { RowsAfter = NextRowOnPage(2) };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(SavePost(
            antiforgeryToken, typed: StoredText, shown: StoredText, saveAction: "next", address: $"/editor/{RowId}?{ListState}"));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        posted.Headers.Location.ShouldNotBeNull().OriginalString.ShouldEndWith($"/editor/{RowId}?{ListState}&saved=true");
        translationSystem.Upserts.Count.ShouldBe(1);
        translationSystem.ListReads.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(StoredText, false)]
    [InlineData(StoredText + "\r\n", true)]
    public async Task Post_SaveAndNext_WhenTheRowDoesNotLoad_ComparesTheTextsExactly(string typed, bool expectSave)
    {
        // Arrange: without the row the English is unknown, so so is its rule for line breaks at the end.
        StubTranslationSystem translationSystem = new() { RowLoads = false, RowsAfter = NextRowOnPage(2) };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(
            SavePost(antiforgeryToken, typed, shown: StoredText, saveAction: "next"));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        posted.Headers.Location.ShouldNotBeNull().OriginalString.ShouldContain($"/editor/{NextRowId}?");
        translationSystem.Upserts.Count.ShouldBe(expectSave ? 1 : 0);
    }

    [Fact]
    public async Task Post_SaveAndNext_TwiceOneAfterTheOther_OpensTheSameNextRowBothTimes()
    {
        // Arrange: a double click that the browser sends as two posts of the same form.
        StubTranslationSystem translationSystem = new() { RowsAfter = NextRowOnPage(2) };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage first = await browser.SendAsync(
            SavePost(antiforgeryToken, typed: "Witaj.", shown: StoredText, saveAction: "next"));
        using HttpResponseMessage second = await browser.SendAsync(
            SavePost(antiforgeryToken, typed: "Witaj.", shown: StoredText, saveAction: "next"));

        // Assert: the shown text did not change, so the second post saves too and the warnings rule still holds.
        first.Headers.Location.ShouldNotBeNull().OriginalString.ShouldEndWith($"&place={NextPlace}&previousSaved=true");
        second.Headers.Location.ShouldNotBeNull().OriginalString.ShouldBe(first.Headers.Location.OriginalString);
        translationSystem.Upserts.Count.ShouldBe(2);
        translationSystem.ListReads.Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task Post_Approve_WithAPlace_StaysOnTheRowAndKeepsThePlace()
    {
        // Arrange
        StubTranslationSystem translationSystem = new() { Approvable = true };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(new HttpRequestMessage(HttpMethod.Post, EditorAddress)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_handler"] = "approve-translation",
                ["__RequestVerificationToken"] = antiforgeryToken
            })
        });

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        posted.Headers.Location.ShouldNotBeNull().OriginalString.ShouldEndWith($"{EditorAddress}&approved=true");
    }

    public EditorSaveAndNextTests(ProductionFrontendFactory factory)
    {
        _factory = factory;
    }

    /// <summary>The page writes one line break before the text, which the browser drops (spec 0018).</summary>
    [GeneratedRegex("""<textarea id="translated"[^>]*>\n(?<text>[^<]*)</textarea>""")]
    private static partial Regex TranslatedTextarea();

    [GeneratedRegex("""<input type="hidden" name="ShownTextField" value="(?<text>[^"]*)""")]
    private static partial Regex ShownTextField();

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

    private static HttpRequestMessage SavePost(
        string antiforgeryToken,
        string typed,
        string shown,
        string? saveAction,
        string address = EditorAddress)
    {
        Dictionary<string, string> fields = new()
        {
            ["_handler"] = "save-translation",
            ["__RequestVerificationToken"] = antiforgeryToken,
            ["FileIdField"] = FileId.ToString(CultureInfo.InvariantCulture),
            ["GossipIdField"] = GossipId.ToString(CultureInfo.InvariantCulture),
            ["DraftField"] = typed,
            ["ShownTextField"] = shown
        };
        if (saveAction is not null)
        {
            fields["SaveAction"] = saveAction;
        }

        return new HttpRequestMessage(HttpMethod.Post, address) { Content = new FormUrlEncodedContent(fields) };
    }

    private static PaginationResponse<TranslationListItemResponse> NextRowOnPage(int page) =>
        new()
        {
            Items =
            [
                new TranslationListItemResponse(
                    TranslationId.Create(Guid.Parse(NextRowId)),
                    FileId,
                    GossipId + 1,
                    "Next.",
                    null,
                    TranslationStatus.Untranslated,
                    null,
                    DateTimeOffset.UnixEpoch)
                {
                    Place = NextPlace
                }
            ],
            Page = page,
            PageSize = 25,
            TotalCount = 60
        };

    private static PaginationResponse<TranslationListItemResponse> NoRowAfterThePlace(int lastPage) =>
        new()
        {
            Items = [],
            Page = lastPage,
            PageSize = 25,
            TotalCount = 60
        };

    /// <summary>
    /// The service document, the row, its save and approve, and the list read with <c>after</c>. A save
    /// answers with the saved text, as the API does.
    /// </summary>
    private sealed class StubTranslationSystem : HttpMessageHandler
    {
        private readonly ConcurrentQueue<UpsertTranslationRequest> _upserts = new();
        private readonly ConcurrentQueue<string> _listReads = new();

        public string? StoredTranslatedText { get; init; } = StoredText;

        public bool RowLoads { get; init; } = true;

        public bool Approvable { get; init; }

        public HttpStatusCode UpsertAnswer { get; init; } = HttpStatusCode.OK;

        /// <summary>Null makes the list read fail.</summary>
        public PaginationResponse<TranslationListItemResponse>? RowsAfter { get; init; }

        public IReadOnlyList<UpsertTranslationRequest> Upserts => [.. _upserts];

        public IReadOnlyList<string> ListReads => [.. _listReads];

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
                    ? StubAnswers.HateoasJson(Row(StoredTranslatedText))
                    : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            if (uri.GetLeftPart(UriPartial.Path) == CollectionHref.AbsoluteUri && request.Method == HttpMethod.Get)
            {
                _listReads.Enqueue(uri.Query);
                return RowsAfter is null
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : StubAnswers.HateoasJson(RowsAfter);
            }

            if (uri == UpsertHref && request.Method == HttpMethod.Put)
            {
                UpsertTranslationRequest upsert = await request.Content.ShouldNotBeNull()
                    .ReadFromJsonAsync<UpsertTranslationRequest>(cancellationToken)
                    ?? throw new InvalidOperationException("An upsert with no body.");
                _upserts.Enqueue(upsert);
                return UpsertAnswer == HttpStatusCode.OK
                    ? StubAnswers.HateoasJson(Row(upsert.TranslatedText))
                    : new HttpResponseMessage(UpsertAnswer);
            }

            if (uri == ApproveHref && request.Method == HttpMethod.Post)
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private TranslationDetailResponse Row(string? translatedText)
        {
            List<LinkDto> links = [new LinkDto(UpsertHref.AbsoluteUri, Rels.Upsert, "PUT")];
            if (Approvable)
            {
                links.Add(new LinkDto(ApproveHref.AbsoluteUri, Rels.Approve, "POST"));
            }

            return new TranslationDetailResponse(
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
                Links = links
            };
        }
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
