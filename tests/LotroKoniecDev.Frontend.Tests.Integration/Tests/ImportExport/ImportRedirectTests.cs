using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using LotroKoniecDev.Frontend.Tests.Integration.Shared;
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
/// #933: the redirect after an import, so the list loads with a renewed token. bUnit cannot bind the posted
/// file, so this signs in through the real OIDC handler and posts the real form against stubbed servers.
/// </summary>
public sealed partial class ImportRedirectTests : IClassFixture<ProductionFrontendFactory>, IDisposable
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
    private static readonly GameVersionId GameVersion = GameVersionId.Create(Guid.NewGuid());
    private static readonly GameVersionId OtherGameVersion = GameVersionId.Create(Guid.NewGuid());

    private readonly ProductionFrontendFactory _factory;
    private readonly RSA _signingRsa = RSA.Create(2048);
    private readonly RsaSecurityKey _signingKey;

    [Fact]
    public async Task Post_WhenTheImportSucceeds_RedirectsToTheImportPageWithoutASecondListLoad()
    {
        // Arrange: the list loads again only after the redirect. A load before it would carry this
        // request's token, which a long import may have outlived (#933).
        StubTranslationSystem translationSystem = new(ImportAnswer.Accepted(new ImportSummary(1, 0, 0, 0, 0, 0, [])));
        StubSignInServer signInServer = new(_signingKey);
        using WebApplicationFactory<Program> host = CreateHost(signInServer, translationSystem);
        using CookieJarBrowser browser = new(host);
        await SignInAsync(browser, signInServer);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/import-export");
        int listLoadsBeforePost = translationSystem.GameVersionsLoads;

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(StubAnswers.ImportPost(antiforgeryToken, GameVersion.Value));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Uri redirectedTo = new(new Uri("https://localhost"), posted.Headers.Location.ShouldNotBeNull());
        redirectedTo.AbsolutePath.ShouldBe("/import-export");
        redirectedTo.Query.ShouldStartWith("?summary=");
        (translationSystem.GameVersionsLoads - listLoadsBeforePost).ShouldBe(1);
    }

    [Fact]
    public async Task Get_TheRedirectAfterASuccessfulImport_ShowsTheWholeSummary()
    {
        // Arrange
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
        using CookieJarBrowser browser = new(host);
        await SignInAsync(browser, signInServer);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/import-export");
        using HttpResponseMessage posted = await browser.SendAsync(StubAnswers.ImportPost(antiforgeryToken, GameVersion.Value));
        Uri redirectedTo = posted.Headers.Location
            ?? throw new InvalidOperationException($"The import answered {posted.StatusCode} with no redirect.");

        // Act
        using HttpResponseMessage redirected = await browser.SendAsync(new HttpRequestMessage(HttpMethod.Get, redirectedTo));

        // Assert
        redirected.StatusCode.ShouldBe(HttpStatusCode.OK);
        string page = WebUtility.HtmlDecode(await redirected.Content.ReadAsStringAsync());
        page.ShouldContain("Import zakończony powodzeniem.");
        StatTileNumbers().Matches(page).Select(match => match.Groups["number"].Value)
            .ShouldBe(["21", "22", "23", "24", "25", "26"]);
        page.ShouldContain("3 previously-removed row(s) re-added with an unchanged source and restored.");
    }

    [Fact]
    public async Task Post_FromTheRedirectAfterAnImport_SendsTheFileToTheVersionTheAdminChoseNow()
    {
        // Arrange: the address names the last import's version; the posted version must win (#933).
        StubTranslationSystem translationSystem = new(ImportAnswer.Accepted(new ImportSummary(1, 0, 0, 0, 0, 0, [])));
        StubSignInServer signInServer = new(_signingKey);
        using WebApplicationFactory<Program> host = CreateHost(signInServer, translationSystem);
        using CookieJarBrowser browser = new(host);
        await SignInAsync(browser, signInServer);
        using HttpResponseMessage firstImport = await browser.SendAsync(StubAnswers.ImportPost(
            await browser.ReadAntiforgeryTokenAsync("/import-export"), OtherGameVersion.Value));
        string redirectedTo = firstImport.Headers.Location?.OriginalString
            ?? throw new InvalidOperationException($"The first import answered {firstImport.StatusCode} with no redirect.");
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync(redirectedTo);

        // Act
        using HttpResponseMessage secondImport = await browser.SendAsync(
            StubAnswers.ImportPost(antiforgeryToken, GameVersion.Value, redirectedTo));

        // Assert
        secondImport.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        translationSystem.ImportedInto.ShouldBe([OtherGameVersion.Value, GameVersion.Value]);
    }

    [Fact]
    public async Task Post_WhenTheImportIsRefused_ShowsTheProblemWithoutARedirectOrASecondListLoad()
    {
        // Arrange: a refused import keeps the list loaded at the start of the request (#933).
        StubTranslationSystem translationSystem = new(ImportAnswer.Refused("Import.EmptyUpload"));
        StubSignInServer signInServer = new(_signingKey);
        using WebApplicationFactory<Program> host = CreateHost(signInServer, translationSystem);
        using CookieJarBrowser browser = new(host);
        await SignInAsync(browser, signInServer);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/import-export");
        int listLoadsBeforePost = translationSystem.GameVersionsLoads;

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(StubAnswers.ImportPost(antiforgeryToken, GameVersion.Value));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        posted.Headers.Location.ShouldBeNull();
        string page = WebUtility.HtmlDecode(await posted.Content.ReadAsStringAsync());
        page.ShouldContain("Plik nie zawiera żadnych tekstów do zaimportowania.");
        page.ShouldNotContain("Import zakończony powodzeniem.");
        (translationSystem.GameVersionsLoads - listLoadsBeforePost).ShouldBe(1);
    }

    [Fact]
    public async Task Post_FromTheRedirectAfterAnImport_WhenTheVersionListFailsToLoad_ShowsTheErrorAndNotTheLastSuccess()
    {
        // Arrange: the second import posts to an address with the first summary, and its list fails, so it
        // never runs. The page must not answer it with the first import's success (#1122).
        StubTranslationSystem translationSystem = new(ImportAnswer.Accepted(new ImportSummary(21, 22, 23, 24, 25, 26, [])));
        StubSignInServer signInServer = new(_signingKey);
        using WebApplicationFactory<Program> host = CreateHost(signInServer, translationSystem);
        using CookieJarBrowser browser = new(host);
        await SignInAsync(browser, signInServer);
        using HttpResponseMessage firstImport = await browser.SendAsync(StubAnswers.ImportPost(
            await browser.ReadAntiforgeryTokenAsync("/import-export"), OtherGameVersion.Value));
        string redirectedTo = firstImport.Headers.Location?.OriginalString
            ?? throw new InvalidOperationException($"The first import answered {firstImport.StatusCode} with no redirect.");
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync(redirectedTo);
        translationSystem.VersionListFails = true;

        // Act
        using HttpResponseMessage secondImport = await browser.SendAsync(
            StubAnswers.ImportPost(antiforgeryToken, GameVersion.Value, redirectedTo));

        // Assert
        secondImport.StatusCode.ShouldBe(HttpStatusCode.OK);
        string page = WebUtility.HtmlDecode(await secondImport.Content.ReadAsStringAsync());
        page.ShouldContain("Usługa jest chwilowo niedostępna. Spróbuj ponownie za chwilę.");
        page.ShouldNotContain("Import zakończony powodzeniem.");
        StatTileNumbers().Matches(page).ShouldBeEmpty();
        translationSystem.ImportedInto.ShouldBe([OtherGameVersion.Value]);
    }

    public void Dispose()
    {
        _signingRsa.Dispose();
    }

    public ImportRedirectTests(ProductionFrontendFactory factory)
    {
        _factory = factory;
        _signingKey = new RsaSecurityKey(_signingRsa) { KeyId = "test-signing-key" };
    }

    [GeneratedRegex("""<span class="num[^"]*">(?<number>\d+)</span>""")]
    private static partial Regex StatTileNumbers();

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

        // Registered after the app's own setup, so these win. Nothing goes to the network.
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

    private static async Task SignInAsync(CookieJarBrowser browser, StubSignInServer signInServer)
    {
        using HttpResponseMessage challenge = await browser.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/auth/login"));
        Uri authorizeUri = challenge.Headers.Location
            ?? throw new InvalidOperationException($"The login answered {challenge.StatusCode} with no redirect.");
        Dictionary<string, string> authorizeQuery = ParseQuery(authorizeUri);
        signInServer.Nonce = authorizeQuery["nonce"];

        using HttpResponseMessage signedIn = await browser.SendAsync(new HttpRequestMessage(
            HttpMethod.Get,
            $"/callback?code=test-code&state={Uri.EscapeDataString(authorizeQuery["state"])}"));

        // A failed callback also answers 302, but to the error page.
        if (signedIn.Headers.Location?.OriginalString != "/")
        {
            throw new InvalidOperationException(
                $"The sign-in failed: the callback answered {signedIn.StatusCode} to {signedIn.Headers.Location}.");
        }
    }

    private static Dictionary<string, string> ParseQuery(Uri uri) =>
        uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));

    private sealed record ImportAnswer(ImportSummary? Summary, string? ErrorCode)
    {
        public static ImportAnswer Accepted(ImportSummary summary) => new(summary, null);

        public static ImportAnswer Refused(string errorCode) => new(null, errorCode);
    }

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
    /// The version list has two versions an admin may import into, each with its own import link.
    /// </summary>
    private sealed class StubTranslationSystem : HttpMessageHandler
    {
        private const string ImportHrefPrefix = $"{TmsOrigin}/advertised/import-into/";

        private readonly ImportAnswer _importAnswer;
        private readonly ConcurrentQueue<Guid> _importedInto = new();
        private int _gameVersionsLoads;

        public int GameVersionsLoads => _gameVersionsLoads;

        /// <summary>When set, the version list answers 503, as it does while the API is down.</summary>
        public bool VersionListFails { get; set; }

        /// <summary>In order, read from the import link each one followed.</summary>
        public IReadOnlyList<Guid> ImportedInto => [.. _importedInto];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri ?? throw new InvalidOperationException("A call with no address.");
            HttpResponseMessage response = uri.GetLeftPart(UriPartial.Path) switch
            {
                $"{TmsOrigin}/" => StubAnswers.HateoasJson(new DiscoveryResponse("tms")
                {
                    Links =
                    [
                        new LinkDto(GameVersionsHref.AbsoluteUri, Rels.GameVersions, "GET"),
                        new LinkDto($"{TmsOrigin}/advertised/contributions", Rels.ContributionDataExport, "GET")
                    ]
                }),
                { } path when path == GameVersionsHref.AbsoluteUri => LoadGameVersions(),
                { } path when path.StartsWith(ImportHrefPrefix, StringComparison.Ordinal) =>
                    Import(Guid.Parse(path[ImportHrefPrefix.Length..])),
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
            if (VersionListFails)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            return StubAnswers.HateoasJson(new CollectionResponse<GameVersionResponse>
            {
                Items = [ImportableVersion(OtherGameVersion, "48.1"), ImportableVersion(GameVersion, "48.0")],
                Links = [new LinkDto(GameVersionsHref.AbsoluteUri, Rels.Register, "POST")]
            });
        }

        private static GameVersionResponse ImportableVersion(GameVersionId id, string version) =>
            new(id, version, DateTimeOffset.UnixEpoch, GameVersionStatus.Unprocessed)
            {
                Links = [new LinkDto($"{ImportHrefPrefix}{id.Value}", Rels.Import, "POST")]
            };

        private HttpResponseMessage Import(Guid gameVersionId)
        {
            _importedInto.Enqueue(gameVersionId);

            return _importAnswer.Summary is { } summary
                ? StubAnswers.HateoasJson(summary)
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
        }
    }
}
