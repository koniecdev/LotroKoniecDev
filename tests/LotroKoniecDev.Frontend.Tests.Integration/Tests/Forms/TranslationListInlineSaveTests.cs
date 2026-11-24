using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
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
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using AuthDiscoveryResponse = LotroKoniecDev.AuthSystem.Contracts.Discovery.DiscoveryResponse;

namespace LotroKoniecDev.Frontend.Tests.Integration.Tests.Forms;

/// <summary>
/// #1493: a row's own save form on the translation list, posted as a browser posts it. The list page is
/// streamed, so the answer starts before a slow save ends: the redirect comes as a streamed template that
/// blazor.web.js follows, and a refused save arrives as a streamed update of the page. bUnit binds no form.
/// </summary>
public sealed partial class TranslationListInlineSaveTests : IClassFixture<ProductionFrontendFactory>
{
    private const string TmsOrigin = "https://tms.staging.invalid";
    private const string AuthOrigin = "https://auth.staging.invalid";
    private const string RowId = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";
    private const string AboveId = "0192a000-0000-7000-8000-0000000000aa";
    private const int FileId = 620756992;
    private const long GossipId = 1002;

    /// <summary>Every list field away from its default, in the order the list writes them (#1452).</summary>
    private const string ListState = "page=2&search=Frodo%20%26%20Sam&match=WholeText&status=Draft&conflictsOnly=true&mine=true&pageSize=25";

    /// <summary>The Polish text a 503 from an API turns into (ApiProblemCopy).</summary>
    private const string ServiceUnavailableMessage = "Usługa jest chwilowo niedostępna. Spróbuj ponownie za chwilę.";

    /// <summary>The names ASP.NET Core gives the typed clients, which are internal to the Frontend.</summary>
    private const string TranslationSystemClientName = "ITranslationSystemClient";
    private const string AuthSystemClientName = "IAuthSystemClient";

    private static readonly Uri UpsertHref = new($"{TmsOrigin}/advertised/upsert");
    private static readonly Uri ListHref = new($"{TmsOrigin}/advertised/translations");

    private readonly ProductionFrontendFactory _factory;

    [Fact]
    public async Task Post_InlineSave_WhenTheSaveGoesThrough_RedirectsToTheSavedRowOfTheSameList()
    {
        // Arrange
        StubTranslationSystem translationSystem = new() { UpsertAnswer = HttpStatusCode.OK };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(InlineSavePost(antiforgeryToken, "Witaj, wędrowcze."));
        string page = await posted.Content.ReadAsStringAsync();
        using HttpResponseMessage redirect = await browser.GetAsync(StreamedRedirectTarget(page));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        page.ShouldNotContain("inline-recovery");
        redirect.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        redirect.Headers.Location.ShouldNotBeNull().OriginalString
            .ShouldEndWith($"/translations?{ListState}&savedRow={RowId}&savedAfter={AboveId}#row-{RowId}");
        translationSystem.Upserts.ShouldBe([new UpsertTranslationRequest(FileId, GossipId, "Witaj, wędrowcze.")]);
        translationSystem.ListReads.ShouldBe(0);
    }

    [Fact]
    public async Task Post_InlineSave_WhenTheSavedTextHasSlips_SaysSoInTheRedirect()
    {
        // Arrange: two spaces in a row, which the English does not have (#1489).
        StubTranslationSystem translationSystem = new() { UpsertAnswer = HttpStatusCode.OK };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(InlineSavePost(antiforgeryToken, "Witaj,  wędrowcze."));
        using HttpResponseMessage redirect = await browser.GetAsync(StreamedRedirectTarget(await posted.Content.ReadAsStringAsync()));

        // Assert
        redirect.Headers.Location.ShouldNotBeNull().OriginalString
            .ShouldEndWith($"&savedRow={RowId}&savedAfter={AboveId}&savedSlips=true#row-{RowId}");
    }

    [Fact]
    public async Task Post_InlineSave_WhenTheApiRefusesTheSave_ShowsTheErrorTheTypedTextAndItsSlipsInTheOpenRow()
    {
        // Arrange: the English has one placeholder and the typed Polish has none, and two spaces in a row.
        StubTranslationSystem translationSystem = new() { UpsertAnswer = HttpStatusCode.BadRequest };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(InlineSavePost(antiforgeryToken, "Witaj,  wędrowcze!"));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        string frame = WebUtility.HtmlDecode(LastStreamedFrame(await posted.Content.ReadAsStringAsync()));
        Match row = RowCell().Match(frame);
        row.Success.ShouldBeTrue();
        string cell = row.Groups["cell"].Value;
        cell.ShouldContain("""<details class="inline-edit" open""");
        InlineTextarea().Match(cell).Groups["text"].Value.ShouldBe("Witaj,  wędrowcze!");
        cell.ShouldContain("Tłumaczenie musi mieć tyle samo znaczników <--DO_NOT_TOUCH!--> co tekst angielski.");
        cell.ShouldContain("W tłumaczeniu są co najmniej dwie spacje z rzędu, a w angielskim nie ma.");
        Regex.Count(frame, Regex.Escape($"value=\"inline-save-{RowId}\"")).ShouldBe(1);
        frame.ShouldNotContain("inline-recovery");
        translationSystem.ListReads.ShouldBe(1);
    }

    [Fact]
    public async Task Post_InlineSave_WhenTheSaveIsRefusedAndTheRowLeftTheList_KeepsTheTextInARecoveryForm()
    {
        // Arrange: another translator's save moved the row off this page before the post.
        StubTranslationSystem translationSystem = new() { UpsertAnswer = HttpStatusCode.Conflict, ListHasTheRow = false };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(InlineSavePost(antiforgeryToken, "Witaj, wędrowcze!"));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        string frame = WebUtility.HtmlDecode(LastStreamedFrame(await posted.Content.ReadAsStringAsync()));
        frame.ShouldContain($"""<div class="inline-recovery" id="row-{RowId}">""");
        frame.ShouldContain("Operacja jest w konflikcie z aktualnym stanem danych.");
        RecoveryTextarea().Match(frame).Groups["text"].Value.ShouldBe("Witaj, wędrowcze!");
        frame.ShouldContain($"name=\"InlineFileId\" value=\"{FileId}\"");
        frame.ShouldContain($"name=\"InlineGossipId\" value=\"{GossipId}\"");
        frame.ShouldContain($"name=\"InlineSavedAfter\" value=\"{AboveId}\"");
        frame.ShouldContain($"action=\"/translations?{ListState}#row-{RowId}\"");
        Regex.Count(frame, Regex.Escape($"value=\"inline-save-{RowId}\"")).ShouldBe(1);
    }

    [Fact]
    public async Task Post_InlineSave_WhenTheListCannotLoad_SavesAndTheRedirectedPageStillConfirmsTheSave()
    {
        // Arrange: #1122, the outage often lasts into the redirect.
        StubTranslationSystem translationSystem = new() { UpsertAnswer = HttpStatusCode.OK, ListAnswer = HttpStatusCode.ServiceUnavailable };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");
        using HttpResponseMessage posted = await browser.SendAsync(InlineSavePost(antiforgeryToken, "Witaj, wędrowcze."));
        using HttpResponseMessage redirect = await browser.GetAsync(StreamedRedirectTarget(await posted.Content.ReadAsStringAsync()));
        Uri redirectedTo = redirect.Headers.Location
            ?? throw new InvalidOperationException($"The redirect answered {redirect.StatusCode} with no address.");

        // Act
        using HttpResponseMessage redirected = await browser.GetAsync(redirectedTo.PathAndQuery);

        // Assert
        redirected.StatusCode.ShouldBe(HttpStatusCode.OK);
        string page = WebUtility.HtmlDecode(await redirected.Content.ReadAsStringAsync());
        page.ShouldContain("Tłumaczenie zapisano.");
        page.ShouldContain(ServiceUnavailableMessage);
        translationSystem.Upserts.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData($"inline-save-{RowId}")]
    [InlineData("bulk-approve")]
    public async Task Post_FromASessionThatHasEnded_SendsTheCallerToSignInAndBackToTheSameList(string formName)
    {
        // Arrange: the form was drawn for a signed-in user, and the session ended before it was sent, so its
        // antiforgery token no longer fits. The list is open to anyone, so no sign-in check stops the post first.
        StubTranslationSystem translationSystem = new();
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");
        browser.SetCookie(SignedInSession.CookieName, "ended");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(
            FormPost($"/translations?{ListState}", formName, antiforgeryToken, InlineFields("Witaj, wędrowcze.")));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Uri redirectedTo = new(new Uri("https://localhost"), posted.Headers.Location.ShouldNotBeNull());
        redirectedTo.AbsolutePath.ShouldBe("/auth/login");
        QueryHelpers.ParseQuery(redirectedTo.Query)["returnUrl"].ToString().ShouldBe($"/translations?{ListState}");
        translationSystem.Upserts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Post_BulkApproveFromAnEndedSession_ComesBackFromTheSignInWithNoOldNote()
    {
        // Arrange: the admin's list after an earlier approve and a save, as their redirects leave it.
        StubTranslationSystem translationSystem = new() { ListOffersBulkApprove = true };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        using HttpResponseMessage list = await browser.GetAsync(
            $"/translations?{ListState}&approved=1&skipped=0&savedRow={RowId}&savedSlips=true");
        string page = await list.Content.ReadAsStringAsync();
        string formAction = WebUtility.HtmlDecode(BulkApproveFormAction().Match(page).Groups["action"].Value);
        string antiforgeryToken = WebUtility.HtmlDecode(AntiforgeryTokenField().Match(page).Groups["token"].Value);
        browser.SetCookie(SignedInSession.CookieName, "ended");

        // Act: the form is sent to the address it names, as a browser sends it.
        using HttpResponseMessage posted = await browser.SendAsync(
            FormPost(formAction, "bulk-approve", antiforgeryToken, new Dictionary<string, string> { [$"SelectedRows[{AboveId}]"] = "true" }));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Uri redirectedTo = new(new Uri("https://localhost"), posted.Headers.Location.ShouldNotBeNull());
        QueryHelpers.ParseQuery(redirectedTo.Query)["returnUrl"].ToString().ShouldBe($"/translations?{ListState}");
    }

    [Theory]
    [InlineData("/does-not-exist")]
    [InlineData("/auth/login")]
    public async Task Post_FromAnEndedSessionToAnAddressWithNoSuchPage_IsNotSentToSignIn(string path)
    {
        // Arrange: the error page that answers this runs as a second, inner request. Its address is not where
        // the form was sent, so a sign-in would only lead back to the error page.
        StubTranslationSystem translationSystem = new();
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");
        browser.SetCookie(SignedInSession.CookieName, "ended");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(FormPost(path, "inline-save-x", antiforgeryToken, []));

        // Assert
        posted.Headers.Location.ShouldBeNull();
    }

    [Fact]
    public async Task Post_FromASignedInSessionWithATokenThatDoesNotFit_StillGetsTheBadRequest()
    {
        // Arrange: only a caller with no session is sent to sign in; a live session with a wrong token is a
        // broken post and keeps Blazor's own answer.
        StubTranslationSystem translationSystem = new();
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(
            FormPost($"/translations?{ListState}", $"inline-save-{RowId}", "not-a-token", InlineFields("Witaj, wędrowcze.")));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        translationSystem.Upserts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Post_InlineSave_WhenTheApiIsDownForTheSaveAndTheList_KeepsTheTextNextToTheLoadError()
    {
        // Arrange: #1122 for the list's own form.
        StubTranslationSystem translationSystem = new()
        {
            UpsertAnswer = HttpStatusCode.ServiceUnavailable,
            ListAnswer = HttpStatusCode.ServiceUnavailable
        };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(InlineSavePost(antiforgeryToken, "Witaj, wędrowcze!"));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        string frame = WebUtility.HtmlDecode(LastStreamedFrame(await posted.Content.ReadAsStringAsync()));
        frame.ShouldContain($"<div class=\"inline-recovery\" id=\"row-{RowId}\">");
        RecoveryTextarea().Match(frame).Groups["text"].Value.ShouldBe("Witaj, wędrowcze!");
        frame.ShouldContain("<div class=\"error-message\" role=\"alert\">");
        frame.ShouldContain(ServiceUnavailableMessage);
    }

    [Fact]
    public async Task Post_InlineSave_WhenTheSaveIsRefusedAndTheRowNoLongerOffersItsSave_ShowsTheTextUnderTheRow()
    {
        // Arrange: the API refused the caller, and the reloaded row has no upsert link, so it draws no form.
        // The answer's address points at the row, so the text and the error must be right under it.
        StubTranslationSystem translationSystem = new() { UpsertAnswer = HttpStatusCode.Forbidden, RowOffersItsSave = false };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(InlineSavePost(antiforgeryToken, "Witaj, wędrowcze!"));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        string frame = WebUtility.HtmlDecode(LastStreamedFrame(await posted.Content.ReadAsStringAsync()));
        Match noteUnderTheRow = RecoveryUnderTheRow().Match(frame);
        noteUnderTheRow.Success.ShouldBeTrue();
        RecoveryTextarea().Match(noteUnderTheRow.Value).Groups["text"].Value.ShouldBe("Witaj, wędrowcze!");
        Regex.Count(frame, Regex.Escape($"id=\"row-{RowId}\"")).ShouldBe(1);
        Regex.Count(frame, Regex.Escape($"value=\"inline-save-{RowId}\"")).ShouldBe(1);
    }

    [Fact]
    public async Task Post_FromAnAddressThatStillHasTheNoteOfASave_ShowsNoOldConfirmation()
    {
        // Arrange: a form posts to the address it was drawn on, so the note's fields come with it (#1122).
        StubTranslationSystem translationSystem = new() { ListOffersBulkApprove = true };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act: the admin sends the bulk approve with nothing ticked.
        using HttpResponseMessage posted = await browser.SendAsync(
            FormPost($"/translations?savedRow={RowId}&savedAfter={AboveId}", "bulk-approve", antiforgeryToken, []));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        string page = WebUtility.HtmlDecode(await posted.Content.ReadAsStringAsync());
        page.ShouldContain("Zaznacz co najmniej jeden wiersz do zatwierdzenia.");
        page.ShouldNotContain("Tłumaczenie zapisano.");
        page.ShouldNotContain("row-note");
    }

    [Theory]
    [InlineData("InlineFileId", "abc")]
    [InlineData("InlineGossipId", "1e3")]
    public async Task Post_InlineSave_WithAKeyFieldThatIsNoNumber_ShowsTheApisRefusalInTheRow(string field, string value)
    {
        // Arrange: the field binds as 0, and the API decides that no such row exists.
        StubTranslationSystem translationSystem = new() { UpsertAnswer = HttpStatusCode.BadRequest };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");
        Dictionary<string, string> fields = InlineFields("Witaj, wędrowcze!");
        fields[field] = value;

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(
            FormPost($"/translations?{ListState}", $"inline-save-{RowId}", antiforgeryToken, fields));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        string frame = WebUtility.HtmlDecode(LastStreamedFrame(await posted.Content.ReadAsStringAsync()));
        RowCell().Match(frame).Groups["cell"].Value
            .ShouldContain("Tłumaczenie musi mieć tyle samo znaczników <--DO_NOT_TOUCH!--> co tekst angielski.");
        translationSystem.Upserts.ShouldHaveSingleItem().TranslatedText.ShouldBe("Witaj, wędrowcze!");
    }

    public TranslationListInlineSaveTests(ProductionFrontendFactory factory)
    {
        _factory = factory;
    }

    /// <summary>The Polish cell of the posted row: from its row to the status cell after it.</summary>
    [GeneratedRegex($"""<tr id="row-{RowId}">.*?<td class="col-text">.*?</td>\s*<td class="col-text">(?<cell>.*?)</td>\s*<td class="col-status">""", RegexOptions.Singleline)]
    private static partial Regex RowCell();

    [GeneratedRegex("""<textarea id="inline-text-[^"]*"[^>]*>(?<text>[^<]*)</textarea>""")]
    private static partial Regex InlineTextarea();

    [GeneratedRegex("""<textarea id="inline-text-recovery"[^>]*>(?<text>[^<]*)</textarea>""")]
    private static partial Regex RecoveryTextarea();

    /// <summary>The posted row, then right under it a note row with the recovery form.</summary>
    [GeneratedRegex($"""<tr id="row-{RowId}">.*?</tr>\s*<tr class="row-note">\s*<td[^>]*>\s*<div class="inline-recovery">.*?</tr>""", RegexOptions.Singleline)]
    private static partial Regex RecoveryUnderTheRow();

    [GeneratedRegex("""<form id="bulk-approve"[^>]*\baction="(?<action>[^"]*)""")]
    private static partial Regex BulkApproveFormAction();

    [GeneratedRegex("""<form id="bulk-approve".*?name="__RequestVerificationToken" value="(?<token>[^"]*)""", RegexOptions.Singleline)]
    private static partial Regex AntiforgeryTokenField();

    /// <summary>Blazor streams each later render of the page as the content of such a template.</summary>
    [GeneratedRegex("""<template blazor-component-id="[^"]*">(?<frame>.*?)</template>""", RegexOptions.Singleline)]
    private static partial Regex StreamedFrame();

    /// <summary>The redirect a streamed page writes once its response has started.</summary>
    [GeneratedRegex("""<template type="redirection" from="form-post">(?<target>[^<]+)</template>""")]
    private static partial Regex StreamedRedirect();

    /// <summary>The last streamed render of the page's content, after the save ended.</summary>
    private static string LastStreamedFrame(string page) =>
        StreamedFrame().Matches(page)
            .Select(frame => frame.Groups["frame"].Value)
            .LastOrDefault(frame => frame.Contains("content-wrap", StringComparison.Ordinal))
        ?? throw new InvalidOperationException("The page streamed no update of its content.");

    /// <summary>
    /// blazor.web.js would follow this address. It is relative to the site root, as the layout's base is.
    /// </summary>
    private static string StreamedRedirectTarget(string page) =>
        StreamedRedirect().Match(page) is { Success: true } redirect
            ? "/" + WebUtility.HtmlDecode(redirect.Groups["target"].Value)
            : throw new InvalidOperationException("The page wrote no streamed redirect.");

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

    private static HttpRequestMessage InlineSavePost(string antiforgeryToken, string text) =>
        FormPost($"/translations?{ListState}", $"inline-save-{RowId}", antiforgeryToken, InlineFields(text));

    private static Dictionary<string, string> InlineFields(string text) =>
        new()
        {
            ["InlineFileId"] = FileId.ToString(CultureInfo.InvariantCulture),
            ["InlineGossipId"] = GossipId.ToString(CultureInfo.InvariantCulture),
            ["InlineSavedAfter"] = AboveId,
            ["InlineText"] = text
        };

    private static HttpRequestMessage FormPost(
        string pathAndQuery,
        string formName,
        string antiforgeryToken,
        Dictionary<string, string> fields)
    {
        Dictionary<string, string> form = new(fields)
        {
            ["_handler"] = formName,
            ["__RequestVerificationToken"] = antiforgeryToken
        };

        return new HttpRequestMessage(HttpMethod.Post, pathAndQuery) { Content = new FormUrlEncodedContent(form) };
    }

    /// <summary>
    /// The service document, the list and the save. The save answers after a yield, as a real network call
    /// does, so the page's answer has started before the save ends.
    /// </summary>
    private sealed class StubTranslationSystem : HttpMessageHandler
    {
        private const string SourceText = "Hello, <--DO_NOT_TOUCH!-->!";

        private readonly ConcurrentQueue<UpsertTranslationRequest> _upserts = new();
        private int _listReads;

        public HttpStatusCode UpsertAnswer { get; init; } = HttpStatusCode.OK;

        public HttpStatusCode ListAnswer { get; init; } = HttpStatusCode.OK;

        public bool ListHasTheRow { get; init; } = true;

        public bool RowOffersItsSave { get; init; } = true;

        /// <summary>The admin's view: the page's bulk-approve link and an approve link on each row.</summary>
        public bool ListOffersBulkApprove { get; init; }

        public IReadOnlyList<UpsertTranslationRequest> Upserts => [.. _upserts];

        public int ListReads => _listReads;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri ?? throw new InvalidOperationException("A call with no address.");
            await Task.Yield();

            if (uri.GetLeftPart(UriPartial.Path) == $"{TmsOrigin}/")
            {
                return StubAnswers.HateoasJson(new DiscoveryResponse("tms")
                {
                    Links =
                    [
                        new LinkDto(ListHref.AbsoluteUri, Rels.Translations, "GET"),
                        new LinkDto(UpsertHref.AbsoluteUri, Rels.Upsert, "PUT")
                    ]
                });
            }

            if (uri.GetLeftPart(UriPartial.Path) == ListHref.AbsoluteUri && request.Method == HttpMethod.Get)
            {
                Interlocked.Increment(ref _listReads);
                return ListAnswer == HttpStatusCode.OK
                    ? StubAnswers.HateoasJson(ListPage())
                    : new HttpResponseMessage(ListAnswer);
            }

            if (uri == UpsertHref && request.Method == HttpMethod.Put && UpsertAnswer >= HttpStatusCode.InternalServerError)
            {
                // The client retries a 5xx with the same request, whose body this stub can read only once.
                return new HttpResponseMessage(UpsertAnswer);
            }

            if (uri == UpsertHref && request.Method == HttpMethod.Put)
            {
                UpsertTranslationRequest upsert = await request.Content.ShouldNotBeNull()
                    .ReadFromJsonAsync<UpsertTranslationRequest>(cancellationToken)
                    ?? throw new InvalidOperationException("An upsert with no body.");
                _upserts.Enqueue(upsert);
                return UpsertAnswer switch
                {
                    HttpStatusCode.OK => StubAnswers.HateoasJson(SavedRow(upsert)),
                    HttpStatusCode.BadRequest => PlaceholderRefusal(),
                    _ => new HttpResponseMessage(UpsertAnswer)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private PaginationResponse<TranslationListItemResponse> ListPage()
        {
            List<TranslationListItemResponse> rows = [ListRow(AboveId, GossipId - 1, offersItsSave: true)];
            if (ListHasTheRow)
            {
                rows.Add(ListRow(RowId, GossipId, RowOffersItsSave));
            }

            return new PaginationResponse<TranslationListItemResponse>
            {
                Items = rows,
                Page = 2,
                PageSize = 25,
                TotalCount = 25 + rows.Count,
                Links = ListOffersBulkApprove ? [new LinkDto($"{ListHref.AbsoluteUri}/approve", Rels.BulkApprove, "POST")] : []
            };
        }

        private TranslationListItemResponse ListRow(string id, long gossipId, bool offersItsSave)
        {
            List<LinkDto> links = [];
            if (offersItsSave)
            {
                links.Add(new LinkDto(UpsertHref.AbsoluteUri, Rels.Upsert, "PUT"));
            }

            if (ListOffersBulkApprove)
            {
                links.Add(new LinkDto($"{ListHref.AbsoluteUri}/{id}/approve", Rels.Approve, "POST"));
            }

            return new TranslationListItemResponse(
                TranslationId.Create(Guid.Parse(id)),
                FileId,
                gossipId,
                SourceText,
                TranslatedText: null,
                TranslationStatus.Draft,
                Submitter: null,
                UpdatedAt: DateTimeOffset.UnixEpoch)
            {
                Links = links
            };
        }

        private static TranslationDetailResponse SavedRow(UpsertTranslationRequest upsert) =>
            new(
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
                UpdatedAt: DateTimeOffset.UnixEpoch);

        private static HttpResponseMessage PlaceholderRefusal()
        {
            ProblemDetails problem = new()
            {
                Status = (int)HttpStatusCode.BadRequest,
                Title = "Validation Error",
                Detail = "The Polish text has 0 placeholder(s) and the English has 1; they must match."
            };
            problem.Extensions["errorCode"] = "TranslationEntity.TranslatedText.PlaceholderCountMismatch";
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = JsonContent.Create(problem, mediaType: new MediaTypeHeaderValue("application/problem+json"))
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
