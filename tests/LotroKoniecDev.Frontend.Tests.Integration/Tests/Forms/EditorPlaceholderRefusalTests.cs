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
/// The editor's answer to a save the translation API refuses for a placeholder mismatch (#1437), on a row
/// that loads. The page has no script, so this page is the translator's only feedback. bUnit cannot show
/// it: it never binds the posted form.
/// </summary>
public sealed partial class EditorPlaceholderRefusalTests : IClassFixture<ProductionFrontendFactory>
{
    private const string TmsOrigin = "https://tms.staging.invalid";
    private const string AuthOrigin = "https://auth.staging.invalid";
    private const string RowId = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";
    private const int FileId = 620756992;
    private const long GossipId = 1002;

    /// <summary>The names ASP.NET Core gives the typed clients, which are internal to the Frontend.</summary>
    private const string TranslationSystemClientName = "ITranslationSystemClient";
    private const string AuthSystemClientName = "IAuthSystemClient";

    private static readonly Uri UpsertHref = new($"{TmsOrigin}/advertised/upsert");
    private static readonly Uri RowHref = new($"{TmsOrigin}/advertised/translations/{RowId}");

    private readonly ProductionFrontendFactory _factory;

    [Fact]
    public async Task Post_SaveForm_WhenTheApiRefusesAPlaceholderMismatch_ShowsTheMessageTheCountsAndTheTypedText()
    {
        // Arrange: the English has one placeholder and the typed Polish has none.
        StubTranslationSystem translationSystem = new();
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(SavePost(antiforgeryToken, "Witaj, wędrowcze!"));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        string page = WebUtility.HtmlDecode(await posted.Content.ReadAsStringAsync());
        page.ShouldContain("Tłumaczenie musi mieć tyle samo znaczników <--DO_NOT_TOUCH!--> co tekst angielski.");
        page.ShouldContain("w tekście angielskim 1,");
        page.ShouldContain("w tłumaczeniu 0.");
        page.ShouldNotContain("The Polish text has 0 placeholder(s)");
        TranslatedTextarea().Match(page).Groups["text"].Value.ShouldBe("Witaj, wędrowcze!");
        translationSystem.Upserts.ShouldBe([new UpsertTranslationRequest(FileId, GossipId, "Witaj, wędrowcze!")]);
    }

    public EditorPlaceholderRefusalTests(ProductionFrontendFactory factory)
    {
        _factory = factory;
    }

    [GeneratedRegex("""<textarea id="translated"[^>]*>\n(?<text>[^<]*)</textarea>""")]
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

    /// <summary>
    /// The service document and the row answer, and every upsert is refused as the API refuses a Polish
    /// text with a different placeholder count than the English.
    /// </summary>
    private sealed class StubTranslationSystem : HttpMessageHandler
    {
        private readonly ConcurrentQueue<UpsertTranslationRequest> _upserts = new();

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
                return PlaceholderRefusal();
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static TranslationDetailResponse Row() =>
            new(
                TranslationId.Create(Guid.Parse(RowId)),
                FileId,
                GossipId,
                SourceText: "Hello, <--DO_NOT_TOUCH!-->!",
                ArgsOrder: "1",
                ArgsId: "1",
                TranslatedText: null,
                PreviousSourceText: null,
                Submitter: null,
                Approver: null,
                TranslationStatus.Untranslated,
                CreatedAt: DateTimeOffset.UnixEpoch,
                UpdatedAt: DateTimeOffset.UnixEpoch)
            {
                Links = [new LinkDto(UpsertHref.AbsoluteUri, Rels.Upsert, "PUT")]
            };

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
