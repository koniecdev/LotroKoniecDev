using System.Collections.Concurrent;
using System.Globalization;
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

namespace LotroKoniecDev.Frontend.Tests.Integration.Tests.Forms;

/// <summary>
/// The editor's slip warnings (#1489) on a posted save, on a row that loads. They only warn, so a text with
/// slips is saved as typed, and after a refused save they follow the typed text. bUnit cannot show either:
/// it never binds the posted form.
/// </summary>
public sealed partial class EditorSlipWarningTests : IClassFixture<ProductionFrontendFactory>
{
    private const string TmsOrigin = "https://tms.staging.invalid";
    private const string AuthOrigin = "https://auth.staging.invalid";
    private const string RowId = "3f2504e0-4f89-41d3-9a0c-0305e82c3302";
    private const int FileId = 620756992;
    private const long GossipId = 1003;

    /// <summary>The stored Polish matches this English on every point, so any slip on the page is the typed text's.</summary>
    private const string SourceText = "Hello.";
    private const string StoredTranslatedText = "Cześć.";

    /// <summary>The names ASP.NET Core gives the typed clients, which are internal to the Frontend.</summary>
    private const string TranslationSystemClientName = "ITranslationSystemClient";
    private const string AuthSystemClientName = "IAuthSystemClient";

    private static readonly Uri UpsertHref = new($"{TmsOrigin}/advertised/upsert");
    private static readonly Uri RowHref = new($"{TmsOrigin}/advertised/translations/{RowId}");

    private readonly ProductionFrontendFactory _factory;

    [Fact]
    public async Task Post_SaveForm_WhenTheTypedTextHasSlips_SavesItAsTypedAndRedirects()
    {
        // Arrange: a space at both ends, a straight quote and no full stop.
        const string typed = " \"Cześć\" ";
        StubTranslationSystem translationSystem = new() { UpsertAnswer = HttpStatusCode.OK };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(SavePost(antiforgeryToken, typed));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        posted.Headers.Location.ShouldNotBeNull().OriginalString.ShouldEndWith($"/editor/{RowId}?saved=true");
        translationSystem.Upserts.ShouldBe([new UpsertTranslationRequest(FileId, GossipId, typed)]);
    }

    [Fact]
    public async Task Post_SaveForm_WhenTheApiRefusesTheSave_ShowsTheSlipsOfTheTypedText()
    {
        // Arrange
        StubTranslationSystem translationSystem = new() { UpsertAnswer = HttpStatusCode.Conflict };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(SavePost(antiforgeryToken, "Cześć "));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        string page = WebUtility.HtmlDecode(await posted.Content.ReadAsStringAsync());
        TranslatedTextarea().Match(page).Groups["text"].Value.ShouldBe("Cześć ");
        page.ShouldContain("Na końcu tłumaczenia jest spacja, a w angielskim nie ma.");
        page.ShouldContain("Inny znak na końcu niż w angielskim: w angielskim kropka (.), w tłumaczeniu brak znaku.");
    }

    public EditorSlipWarningTests(ProductionFrontendFactory factory)
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

    private static TranslationDetailResponse Row() =>
        new(
            TranslationId.Create(Guid.Parse(RowId)),
            FileId,
            GossipId,
            SourceText,
            ArgsOrder: null,
            ArgsId: null,
            StoredTranslatedText,
            PreviousSourceText: null,
            Submitter: null,
            Approver: null,
            TranslationStatus.Draft,
            CreatedAt: DateTimeOffset.UnixEpoch,
            UpdatedAt: DateTimeOffset.UnixEpoch)
        {
            Links = [new LinkDto(UpsertHref.AbsoluteUri, Rels.Upsert, "PUT")]
        };

    /// <summary>The service document and the row answer, and the upsert answers as the test sets.</summary>
    private sealed class StubTranslationSystem : HttpMessageHandler
    {
        private readonly ConcurrentQueue<UpsertTranslationRequest> _upserts = new();

        public HttpStatusCode UpsertAnswer { get; init; } = HttpStatusCode.ServiceUnavailable;

        public IReadOnlyList<UpsertTranslationRequest> Upserts => [.. _upserts];

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
                        new LinkDto($"{TmsOrigin}/advertised/translations", Rels.Translations, "GET"),
                        new LinkDto(UpsertHref.AbsoluteUri, Rels.Upsert, "PUT")
                    ]
                });
            }

            if (uri == RowHref && request.Method == HttpMethod.Get)
            {
                return StubAnswers.HateoasJson(Row());
            }

            if (uri == UpsertHref && request.Method == HttpMethod.Put)
            {
                UpsertTranslationRequest upsert = await request.Content.ShouldNotBeNull()
                    .ReadFromJsonAsync<UpsertTranslationRequest>(cancellationToken)
                    ?? throw new InvalidOperationException("An upsert with no body.");
                _upserts.Enqueue(upsert);
                return UpsertAnswer == HttpStatusCode.OK
                    ? StubAnswers.HateoasJson(Row())
                    : new HttpResponseMessage(UpsertAnswer);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
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
