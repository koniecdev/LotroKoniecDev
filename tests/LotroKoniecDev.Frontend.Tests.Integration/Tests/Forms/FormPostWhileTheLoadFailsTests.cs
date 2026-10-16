using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Contracts.Discovery;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using AuthDiscoveryResponse = LotroKoniecDev.AuthSystem.Contracts.Discovery.DiscoveryResponse;
using AuthRels = LotroKoniecDev.AuthSystem.Contracts.Hateoas.Rels;

namespace LotroKoniecDev.Frontend.Tests.Integration.Tests.Forms;

/// <summary>
/// #1122: a page draws its form only when its data loaded. Blazor sends a post only to a form that is on
/// the page, and outside Development it answers a post to a missing form with an empty 400, which the
/// user sees as a blank page. These tests send each page's real form post, with antiforgery, while the API
/// the page loads from is down, and check that the user gets the page with its Polish error instead.
/// bUnit cannot show this: it calls a form's handler directly and never looks the form up by name.
/// </summary>
public sealed partial class FormPostWhileTheLoadFailsTests : IClassFixture<StagingFrontendFactory>
{
    private const string AuthCookieName = ".lotrokoniecdev.auth";
    private const string TmsOrigin = "https://tms.staging.invalid";
    private const string AuthOrigin = "https://auth.staging.invalid";
    private const string Subject = "11111111-1111-1111-1111-111111111111";
    private const string RowId = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";

    /// <summary>The Polish text a 503 from an API turns into (ApiProblemCopy).</summary>
    private const string ServiceUnavailableMessage = "Usługa jest chwilowo niedostępna. Spróbuj ponownie za chwilę.";

    /// <summary>The names ASP.NET Core gives the typed clients, which are internal to the Frontend.</summary>
    private const string TranslationSystemClientName = "ITranslationSystemClient";
    private const string AuthSystemClientName = "IAuthSystemClient";

    private static readonly Uri UpsertHref = new($"{TmsOrigin}/advertised/upsert");

    /// <summary>The options the Frontend reads the TMS answers with (HttpClientApiExtensions).</summary>
    private static readonly JsonSerializerOptions ApiJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly StagingFrontendFactory _factory;
    private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);

    [Fact]
    public async Task Post_ImportForm_WhenTheVersionListFailsToLoad_ShowsTheImportPageWithTheLoadError()
    {
        // Arrange: the case the ticket was found with. The admin sends exported.txt, and the list of
        // versions the page loads first cannot be read in that request.
        StubTranslationSystem translationSystem = new();
        StubAuthSystem authSystem = new(HttpStatusCode.ServiceUnavailable);
        using WebApplicationFactory<Program> host = CreateHost(translationSystem, authSystem);
        using HttpClient browser = CreateBrowser(host);
        string antiforgeryToken = await SignInAndReadAntiforgeryTokenAsync(browser, host);

        // Act
        using HttpResponseMessage posted = await SendAsync(browser, ImportPost(antiforgeryToken));

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
        using HttpClient browser = CreateBrowser(host);
        string antiforgeryToken = await SignInAndReadAntiforgeryTokenAsync(browser, host);

        // Act
        using HttpResponseMessage posted = await SendAsync(browser, FormPost(path, formName, antiforgeryToken));

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
        using HttpClient browser = CreateBrowser(host);
        string antiforgeryToken = await SignInAndReadAntiforgeryTokenAsync(browser, host);

        // Act
        using HttpResponseMessage posted = await SendAsync(browser, FormPost(path, formName, antiforgeryToken));

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
        using HttpClient browser = CreateBrowser(host);
        string antiforgeryToken = await SignInAndReadAntiforgeryTokenAsync(browser, host);

        // Act
        using HttpResponseMessage posted = await SendAsync(browser, SavePost(antiforgeryToken, "Witaj, wędrowcze."));

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
        using HttpClient browser = CreateBrowser(host);
        string antiforgeryToken = await SignInAndReadAntiforgeryTokenAsync(browser, host);

        // Act
        using HttpResponseMessage posted = await SendAsync(browser, SavePost(antiforgeryToken, "Witaj, wędrowcze."));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        string page = WebUtility.HtmlDecode(await posted.Content.ReadAsStringAsync());
        page.ShouldContain("Operacja jest w konflikcie z aktualnym stanem danych.");
        RecoveryTextarea().Match(page).Groups["text"].Value.ShouldBe("Witaj, wędrowcze.");
        page.ShouldContain("name=\"FileIdField\" value=\"620756992\"");
        page.ShouldContain("name=\"GossipIdField\" value=\"1002\"");
    }

    [Fact]
    public async Task Post_NamingAFormThePageNeverHas_WhileTheLoadFails_StillGetsTheBadRequest()
    {
        // Arrange: only the page's own form names are kept. A post to a name no page ever drew is a broken
        // request and keeps Blazor's own answer.
        StubTranslationSystem translationSystem = new();
        StubAuthSystem authSystem = new(HttpStatusCode.ServiceUnavailable);
        using WebApplicationFactory<Program> host = CreateHost(translationSystem, authSystem);
        using HttpClient browser = CreateBrowser(host);
        string antiforgeryToken = await SignInAndReadAntiforgeryTokenAsync(browser, host);

        // Act
        using HttpResponseMessage posted = await SendAsync(
            browser, FormPost("/import-export", "no-such-form", antiforgeryToken));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    public FormPostWhileTheLoadFailsTests(StagingFrontendFactory factory)
    {
        _factory = factory;
    }

    [GeneratedRegex("""name="__RequestVerificationToken" value="(?<token>[^"]+)""")]
    private static partial Regex AntiforgeryTokenInput();

    [GeneratedRegex("""<textarea id="translated-recovery"[^>]*>(?<text>[^<]*)</textarea>""")]
    private static partial Regex RecoveryTextarea();

    /// <summary>
    /// The sign-in server is never called: the session cookie is made with the host's own keys, and its
    /// access token is far from expiry, so the cookie check has no renewal to make.
    /// </summary>
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

    private static HttpClient CreateBrowser(WebApplicationFactory<Program> host) =>
        host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost")
        });

    /// <summary>
    /// Puts a signed-in session into the cookie jar and reads an antiforgery token from the sign-out form
    /// every page has for a signed-in user. The token belongs to the session, not to one form.
    /// </summary>
    private async Task<string> SignInAndReadAntiforgeryTokenAsync(HttpClient browser, WebApplicationFactory<Program> host)
    {
        _cookies[AuthCookieName] = ProtectedSessionTicket(host);

        using HttpResponseMessage page = await SendAsync(browser, new HttpRequestMessage(HttpMethod.Get, "/terms"));
        Match antiforgeryToken = AntiforgeryTokenInput().Match(await page.Content.ReadAsStringAsync());

        return antiforgeryToken.Success
            ? antiforgeryToken.Groups["token"].Value
            : throw new InvalidOperationException($"The terms page answered {page.StatusCode} with no sign-out form.");
    }

    private static string ProtectedSessionTicket(WebApplicationFactory<Program> host)
    {
        ClaimsPrincipal user = new(new ClaimsIdentity(
            [new Claim("sub", Subject), new Claim("name", "Anna")],
            authenticationType: OpenIdConnectDefaults.AuthenticationScheme,
            nameType: "name",
            roleType: "role"));

        AuthenticationProperties properties = new();
        properties.StoreTokens(
        [
            new AuthenticationToken { Name = "access_token", Value = "opaque-access-token" },
            new AuthenticationToken { Name = "refresh_token", Value = "opaque-refresh-token" },
            new AuthenticationToken
            {
                Name = "expires_at",
                Value = DateTimeOffset.UtcNow.AddMinutes(5).ToString("o", CultureInfo.InvariantCulture)
            }
        ]);

        CookieAuthenticationOptions cookieOptions = host.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);

        return cookieOptions.TicketDataFormat.Protect(
            new AuthenticationTicket(user, properties, CookieAuthenticationDefaults.AuthenticationScheme));
    }

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

    /// <summary>The import form as a browser sends it: multipart, with the file.</summary>
    private static HttpRequestMessage ImportPost(string antiforgeryToken)
    {
        ByteArrayContent file = new("1||1||Hello||NULL||NULL||0000000000000000\n"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

        MultipartFormDataContent content = new()
        {
            { new StringContent("import-export"), "_handler" },
            { new StringContent(antiforgeryToken), "__RequestVerificationToken" },
            { new StringContent(Guid.NewGuid().ToString()), "ImportInput.GameVersionId" },
            { file, "ImportInput.File", "exported.txt" }
        };

        return new HttpRequestMessage(HttpMethod.Post, "/import-export") { Content = content };
    }

    /// <summary>A browser with a plain cookie jar: it keeps what the server sets and sends it back.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpClient browser, HttpRequestMessage request)
    {
        using (request)
        {
            request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(cookie => $"{cookie.Key}={cookie.Value}")));

            HttpResponseMessage response = await browser.SendAsync(request);
            if (response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? setCookies))
            {
                foreach (string[] pair in setCookies.Select(setCookie => setCookie.Split(';', 2)[0].Split('=', 2)))
                {
                    if (pair[1].Length == 0)
                    {
                        _cookies.Remove(pair[0]);
                    }
                    else
                    {
                        _cookies[pair[0]] = pair[1];
                    }
                }
            }

            return response;
        }
    }

    private static HttpResponseMessage Json<T>(T body) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(
                body,
                mediaType: new MediaTypeHeaderValue(MediaTypes.HateoasJson),
                options: ApiJsonOptions)
        };

    /// <summary>A sign-in server that is down: every call fails before an answer arrives.</summary>
    private sealed class UnreachableServer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("Connection refused."));
    }

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
                return Json(new DiscoveryResponse("tms")
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
                ? Json(new TranslationDetailResponse(
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
                $"{AuthOrigin}/" => Json(new AuthDiscoveryResponse("auth")
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
