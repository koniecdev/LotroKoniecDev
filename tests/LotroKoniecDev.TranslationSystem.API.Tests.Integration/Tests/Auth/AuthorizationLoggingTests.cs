using System.Net.Http.Headers;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.TranslationSystem.API.Middleware;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// #854: every call the API refuses with 401 or 403 leaves a warning. It names the client the rate
/// limiter counts and the connection's own address (ADR-0054, amended by #854). Each test boots a derived
/// host whose logger factory captures what the host logged. In Testing <c>UseForwardedHeaders</c> trusts
/// every peer, so <c>X-Forwarded-For</c> plays the connection address Caddy resolves: <c>10.60.0.x</c> is
/// the frontend container, RFC 5737 addresses are visitors.
/// </summary>
[Collection("TranslationApi")]
public sealed class AuthorizationLoggingTests : IAsyncLifetime
{
    // Built rather than written out, so no secret scanner mistakes test data for a key.
    private static readonly string FrontendKey = new('k', 40);

    private const string FrontendAddress = "10.60.0.7";
    private const string ForwardedForHeader = "X-Forwarded-For";
    private const string GameVersionsPath = "/api/v1/game-versions";

    private readonly TranslationSystemApiFactory _factory;

    public AuthorizationLoggingTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync(
            "TRUNCATE translation.\"Translations\", translation.\"GameVersions\", translation.\"Translators\" CASCADE;");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GetGameVersions_WithoutAToken_ShouldWarnAboutTheUnauthorizedCall()
    {
        // Arrange
        using CapturingLoggerFactory loggerFactory = new();
        using WebApplicationFactory<Program> host = CreateHost(loggerFactory);
        using HttpClient client = host.CreateClient();
        using HttpRequestMessage request = CreateRequest(GameVersionsPath, "203.0.113.80");

        // Act
        using HttpResponseMessage response = await client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        CapturingLoggerFactory.LogEntry warning = MiddlewareEntries(loggerFactory).ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.EventId.Id.ShouldBe(EventIds.UnauthorizedAccessAttempt);
        warning.Message.ShouldBe($"Unauthorized access attempt: GET {GameVersionsPath} from 203.0.113.80 via 203.0.113.80");
    }

    [Fact]
    public async Task GetGameVersions_WithARoleWithoutAccess_ShouldWarnAboutTheForbiddenCall()
    {
        // Arrange: a correctly signed token whose role the translator policy does not accept
        using CapturingLoggerFactory loggerFactory = new();
        using WebApplicationFactory<Program> host = CreateHost(loggerFactory);
        using HttpClient client = host.CreateClient();
        using HttpRequestMessage request = CreateRequest(GameVersionsPath, "203.0.113.81");
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", TranslationSystemApiFactory.CreateAccessToken("Reviewer"));

        // Act
        using HttpResponseMessage response = await client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        CapturingLoggerFactory.LogEntry warning = MiddlewareEntries(loggerFactory).ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.EventId.Id.ShouldBe(EventIds.ForbiddenAccessAttempt);
        warning.Message.ShouldBe(
            $"Forbidden access attempt: GET {GameVersionsPath} from 203.0.113.81 via 203.0.113.81 by {TranslationSystemApiFactory.TestUserDisplayName}");
    }

    [Theory]
    [InlineData(true, "203.0.113.82")]
    [InlineData(false, FrontendAddress)]
    public async Task GetGameVersions_WhenRefusedThroughTheFrontend_ShouldNameTheClientAndTheConnection(
        bool keyMatches,
        string namedClient)
    {
        // Arrange: the frontend forwards a visitor. Only the right key makes the forwarded address the
        // client, and the connection's own address is in the line either way.
        using CapturingLoggerFactory loggerFactory = new();
        using WebApplicationFactory<Program> host = CreateHost(loggerFactory);
        using HttpClient client = host.CreateClient();
        using HttpRequestMessage request = CreateRequest(GameVersionsPath, FrontendAddress);
        request.Headers.Add(FrontendCallerHeaders.Key, keyMatches ? FrontendKey : new string('w', 40));
        request.Headers.Add(FrontendCallerHeaders.ClientAddress, "203.0.113.82");

        // Act
        using HttpResponseMessage response = await client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        MiddlewareEntries(loggerFactory).ShouldHaveSingleItem().Message
            .ShouldBe($"Unauthorized access attempt: GET {GameVersionsPath} from {namedClient} via {FrontendAddress}");
    }

    [Fact]
    public async Task GetUnknownRoute_WithoutAToken_ShouldNotWarn()
    {
        // Arrange: the fallback policy refuses a path that matches no endpoint, and no rate limit caps it
        using CapturingLoggerFactory loggerFactory = new();
        using WebApplicationFactory<Program> host = CreateHost(loggerFactory);
        using HttpClient client = host.CreateClient();
        using HttpRequestMessage request = CreateRequest("/does-not-exist", "203.0.113.84");

        // Act
        using HttpResponseMessage response = await client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        MiddlewareEntries(loggerFactory).ShouldBeEmpty();
    }

    [Fact]
    public async Task GetGameVersions_WithAccess_ShouldNotWarn()
    {
        // Arrange
        using CapturingLoggerFactory loggerFactory = new();
        using WebApplicationFactory<Program> host = CreateHost(loggerFactory);
        using HttpClient client = host.CreateClient();
        using HttpRequestMessage request = CreateRequest(GameVersionsPath, "203.0.113.83");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateAccessToken(AuthConstants.Roles.Translator));

        // Act
        using HttpResponseMessage response = await client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        MiddlewareEntries(loggerFactory).ShouldBeEmpty();
    }

    private static List<CapturingLoggerFactory.LogEntry> MiddlewareEntries(CapturingLoggerFactory loggerFactory) =>
        loggerFactory.Entries
            .Where(entry => entry.Category == typeof(AuthorizationLoggingMiddleware).FullName)
            .ToList();

    private static HttpRequestMessage CreateRequest(string path, string connectionAddress)
    {
        HttpRequestMessage request = new(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Add(ForwardedForHeader, connectionAddress);
        return request;
    }

    private WebApplicationFactory<Program> CreateHost(CapturingLoggerFactory loggerFactory)
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "FrontendCaller:Key", FrontendKey }
                });
            });

            builder.ConfigureTestServices(services => services.AddSingleton<ILoggerFactory>(loggerFactory));
        });
    }
}
