using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.Discovery;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Contracts.Import;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace LotroKoniecDev.Frontend.Tests.Integration.Tests.ImportExport;

/// <summary>
/// #933: after a successful import the page redirects, so the version list loads in a new request with a
/// renewed token, and the summary survives the redirect. bUnit cannot bind the posted file, so this test
/// signs in through the real OIDC handler and posts the real form, with antiforgery, against stubbed
/// sign-in and translation servers.
/// </summary>
public sealed partial class ImportRedirectTests : IClassFixture<StagingFrontendFactory>, IDisposable
{
    private const string ClientId = "lotrokoniecdev-web";
    private const string Issuer = "https://auth.staging.invalid";
    private const string TmsOrigin = "https://tms.staging.invalid";
    private const string Subject = "11111111-1111-1111-1111-111111111111";

    /// <summary>The name ASP.NET Core gives the typed client, which is internal to the Frontend.</summary>
    private const string TranslationSystemClientName = "ITranslationSystemClient";

    private static readonly Uri AuthorizationEndpoint = new($"{Issuer}/connect/authorize");
    private static readonly Uri TokenEndpoint = new($"{Issuer}/connect/token");
    private static readonly Uri UserInfoEndpoint = new($"{Issuer}/connect/userinfo");
    private static readonly Uri GameVersionsHref = new($"{TmsOrigin}/advertised/game-versions");
    private static readonly Uri ImportHref = new($"{TmsOrigin}/advertised/import-into/48");
    private static readonly GameVersionId GameVersion = GameVersionId.Create(Guid.NewGuid());

    /// <summary>The options the Frontend reads the TMS answers with (HttpClientApiExtensions).</summary>
    private static readonly JsonSerializerOptions ApiJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly StagingFrontendFactory _factory;
    private readonly RSA _signingRsa = RSA.Create(2048);
    private readonly RsaSecurityKey _signingKey;
    private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);

    [Fact]
    public async Task Post_WhenTheImportSucceeds_RedirectsWithoutASecondListLoadAndTheNextPageShowsTheSummary()
    {
        // Arrange: the list is loaded again only in the request the redirect starts, which renews the token
        // first. A second load before the redirect would carry this request's token, which an import of
        // several minutes may have outlived (#933).
        StubTranslationSystem translationSystem = new(ImportAnswer.Accepted(new ImportSummary(
            Added: 21,
            SourceChanged: 22,
            Invalidated: 23,
            Removed: 24,
            Unchanged: 25,
            Echoed: 26,
            Warnings: ["3 previously-removed row(s) re-added with an unchanged source and restored."])));
        StubSignInServer signInServer = new(_signingKey);
        using WebApplicationFactory<Program> host = CreateHost(signInServer, translationSystem);
        using HttpClient browser = CreateBrowser(host);
        await SignInAsync(browser, signInServer);
        string antiforgeryToken = await LoadImportFormAsync(browser);
        int listLoadsBeforePost = translationSystem.GameVersionsLoads;

        // Act
        using HttpResponseMessage posted = await SendAsync(browser, ImportPost(antiforgeryToken));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (translationSystem.GameVersionsLoads - listLoadsBeforePost).ShouldBe(1);
        Uri redirectedTo = new(new Uri("https://localhost"), posted.Headers.Location.ShouldNotBeNull());
        redirectedTo.AbsolutePath.ShouldBe("/import-export");
        redirectedTo.Query.ShouldStartWith("?summary=");

        using HttpResponseMessage redirected = await SendAsync(browser, new HttpRequestMessage(HttpMethod.Get, redirectedTo));
        redirected.StatusCode.ShouldBe(HttpStatusCode.OK);
        string page = WebUtility.HtmlDecode(await redirected.Content.ReadAsStringAsync());
        page.ShouldContain("Import zakończony powodzeniem.");
        StatTileNumbers().Matches(page).Select(match => match.Groups["number"].Value)
            .ShouldBe(["21", "22", "23", "24", "25", "26"]);
        page.ShouldContain("3 previously-removed row(s) re-added with an unchanged source and restored.");
    }

    [Fact]
    public async Task Post_WhenTheImportIsRefused_ShowsTheProblemWithoutARedirectOrASecondListLoad()
    {
        // Arrange: a refused import changes nothing, so the list loaded at the start of the request stays.
        // A second load would carry this request's token, which a long import may have outlived (#933).
        StubTranslationSystem translationSystem = new(ImportAnswer.Refused("Import.EmptyUpload"));
        StubSignInServer signInServer = new(_signingKey);
        using WebApplicationFactory<Program> host = CreateHost(signInServer, translationSystem);
        using HttpClient browser = CreateBrowser(host);
        await SignInAsync(browser, signInServer);
        string antiforgeryToken = await LoadImportFormAsync(browser);
        int listLoadsBeforePost = translationSystem.GameVersionsLoads;

        // Act
        using HttpResponseMessage posted = await SendAsync(browser, ImportPost(antiforgeryToken));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        posted.Headers.Location.ShouldBeNull();
        string page = WebUtility.HtmlDecode(await posted.Content.ReadAsStringAsync());
        page.ShouldContain("Plik nie zawiera żadnych tekstów do zaimportowania.");
        page.ShouldNotContain("Import zakończony powodzeniem.");
        (translationSystem.GameVersionsLoads - listLoadsBeforePost).ShouldBe(1);
    }

    public void Dispose()
    {
        _signingRsa.Dispose();
    }

    public ImportRedirectTests(StagingFrontendFactory factory)
    {
        _factory = factory;
        _signingKey = new RsaSecurityKey(_signingRsa) { KeyId = "test-signing-key" };
    }

    [GeneratedRegex("""<span class="num[^"]*">(?<number>\d+)</span>""")]
    private static partial Regex StatTileNumbers();

    [GeneratedRegex("""name="__RequestVerificationToken" value="(?<token>[^"]+)""")]
    private static partial Regex AntiforgeryTokenInput();

    private WebApplicationFactory<Program> CreateHost(
        StubSignInServer signInServer,
        StubTranslationSystem translationSystem)
    {
        OpenIdConnectConfiguration configuration = new()
        {
            Issuer = Issuer,
            AuthorizationEndpoint = AuthorizationEndpoint.AbsoluteUri,
            TokenEndpoint = TokenEndpoint.AbsoluteUri,
            UserInfoEndpoint = UserInfoEndpoint.AbsoluteUri
        };
        configuration.SigningKeys.Add(_signingKey);

        // Registered after the app's own setup, so these win: the OIDC handler reads the stub's endpoints
        // and keys, and the TMS client sends every call to the stub. Nothing goes to the network.
        return _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
            {
                options.Configuration = configuration;
                options.BackchannelHttpHandler = signInServer;
            });
            services.AddHttpClient(TranslationSystemClientName)
                .ConfigurePrimaryHttpMessageHandler(() => translationSystem);
        }));
    }

    private static HttpClient CreateBrowser(WebApplicationFactory<Program> host) =>
        host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost")
        });

    private async Task SignInAsync(HttpClient browser, StubSignInServer signInServer)
    {
        using HttpResponseMessage challenge = await SendAsync(
            browser, new HttpRequestMessage(HttpMethod.Get, "/auth/login"));
        Uri authorizeUri = challenge.Headers.Location
            ?? throw new InvalidOperationException($"The login answered {challenge.StatusCode} with no redirect.");
        Dictionary<string, string> authorizeQuery = ParseQuery(authorizeUri);
        signInServer.Nonce = authorizeQuery["nonce"];

        using HttpResponseMessage signedIn = await SendAsync(
            browser,
            new HttpRequestMessage(
                HttpMethod.Get,
                $"/callback?code=test-code&state={Uri.EscapeDataString(authorizeQuery["state"])}"));

        // A failed callback also answers 302, but to the error page.
        if (signedIn.Headers.Location?.OriginalString != "/")
        {
            throw new InvalidOperationException(
                $"The sign-in failed: the callback answered {signedIn.StatusCode} to {signedIn.Headers.Location}.");
        }
    }

    private async Task<string> LoadImportFormAsync(HttpClient browser)
    {
        using HttpResponseMessage form = await SendAsync(browser, new HttpRequestMessage(HttpMethod.Get, "/import-export"));
        string page = await form.Content.ReadAsStringAsync();
        Match antiforgeryToken = AntiforgeryTokenInput().Match(page);

        return antiforgeryToken.Success
            ? antiforgeryToken.Groups["token"].Value
            : throw new InvalidOperationException($"The import page answered {form.StatusCode} with no import form.");
    }

    private static HttpRequestMessage ImportPost(string antiforgeryToken)
    {
        ByteArrayContent file = new("1||1||Hello||NULL||NULL||0000000000000000\n"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

        MultipartFormDataContent content = new()
        {
            { new StringContent("import-export"), "_handler" },
            { new StringContent(antiforgeryToken), "__RequestVerificationToken" },
            { new StringContent(GameVersion.Value.ToString()), "ImportInput.GameVersionId" },
            { file, "ImportInput.File", "exported.txt" }
        };

        return new HttpRequestMessage(HttpMethod.Post, "/import-export") { Content = content };
    }

    /// <summary>A browser with a plain cookie jar: it keeps what the server sets and sends it back.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpClient browser, HttpRequestMessage request)
    {
        using (request)
        {
            if (_cookies.Count > 0)
            {
                request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(cookie => $"{cookie.Key}={cookie.Value}")));
            }

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

    private static Dictionary<string, string> ParseQuery(Uri uri) =>
        uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));

    /// <summary>What the stubbed import endpoint answers: a summary, or a refusal with an error code.</summary>
    private sealed record ImportAnswer(ImportSummary? Summary, string? ErrorCode)
    {
        public static ImportAnswer Accepted(ImportSummary summary) => new(summary, null);

        public static ImportAnswer Refused(string errorCode) => new(null, errorCode);
    }

    /// <summary>
    /// The token and userinfo endpoints of the sign-in server, as the OIDC handler's back-channel sees them.
    /// </summary>
    private sealed class StubSignInServer : HttpMessageHandler
    {
        private readonly SecurityKey _signingKey;

        public string? Nonce { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            HttpResponseMessage response = request.RequestUri switch
            {
                { } uri when uri == TokenEndpoint => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new Dictionary<string, object?>
                    {
                        ["access_token"] = Mint(audience: "lotrokoniecdev-api", tokenType: "at+jwt", nonce: null),
                        ["token_type"] = "Bearer",
                        ["expires_in"] = 300,
                        ["refresh_token"] = "first-refresh-token",
                        ["id_token"] = Mint(audience: ClientId, tokenType: "JWT", nonce: Nonce)
                    })
                },
                { } uri when uri == UserInfoEndpoint => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new Dictionary<string, object?> { ["sub"] = Subject })
                },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };

            return Task.FromResult(response);
        }

        public StubSignInServer(SecurityKey signingKey)
        {
            _signingKey = signingKey;
        }

        private string Mint(string audience, string tokenType, string? nonce)
        {
            List<Claim> claims = [new("sub", Subject)];
            if (nonce is not null)
            {
                claims.Add(new Claim("nonce", nonce));
            }

            SecurityTokenDescriptor descriptor = new()
            {
                TokenType = tokenType,
                Issuer = Issuer,
                Audience = audience,
                Subject = new ClaimsIdentity(claims),
                IssuedAt = DateTime.UtcNow,
                Expires = DateTime.UtcNow.AddMinutes(5),
                SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256)
            };

            return new JsonWebTokenHandler().CreateToken(descriptor);
        }
    }

    /// <summary>
    /// The translation API as the Frontend's typed client sees it: the service document, the version list
    /// with one version an admin may import into, and that version's import link.
    /// </summary>
    private sealed class StubTranslationSystem : HttpMessageHandler
    {
        private readonly ImportAnswer _importAnswer;
        private int _gameVersionsLoads;

        public int GameVersionsLoads => _gameVersionsLoads;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri ?? throw new InvalidOperationException("A call with no address.");
            HttpResponseMessage response = uri.GetLeftPart(UriPartial.Path) switch
            {
                $"{TmsOrigin}/" => Json(new DiscoveryResponse("tms")
                {
                    Links =
                    [
                        new LinkDto(GameVersionsHref.AbsoluteUri, Rels.GameVersions, "GET"),
                        new LinkDto($"{TmsOrigin}/advertised/contributions", Rels.ContributionDataExport, "GET")
                    ]
                }),
                { } path when path == GameVersionsHref.AbsoluteUri => LoadGameVersions(),
                { } path when path == ImportHref.AbsoluteUri => Import(),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };

            return Task.FromResult(response);
        }

        public StubTranslationSystem(ImportAnswer importAnswer)
        {
            _importAnswer = importAnswer;
        }

        private HttpResponseMessage LoadGameVersions()
        {
            Interlocked.Increment(ref _gameVersionsLoads);

            return Json(new CollectionResponse<GameVersionResponse>
            {
                Items =
                [
                    new GameVersionResponse(GameVersion, "48.0", DateTimeOffset.UnixEpoch, GameVersionStatus.Unprocessed)
                    {
                        Links = [new LinkDto(ImportHref.AbsoluteUri, Rels.Import, "POST")]
                    }
                ],
                Links = [new LinkDto(GameVersionsHref.AbsoluteUri, Rels.Register, "POST")]
            });
        }

        private HttpResponseMessage Import() =>
            _importAnswer.Summary is { } summary
                ? Json(summary)
                : new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
                {
                    Content = JsonContent.Create(
                        new Dictionary<string, object?>
                        {
                            ["title"] = "Import refused",
                            ["status"] = 422,
                            ["errorCode"] = _importAnswer.ErrorCode
                        },
                        mediaType: new MediaTypeHeaderValue("application/problem+json"))
                };

        private static HttpResponseMessage Json<T>(T body) =>
            new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(
                    body,
                    mediaType: new MediaTypeHeaderValue(MediaTypes.HateoasJson),
                    options: ApiJsonOptions)
            };
    }
}
