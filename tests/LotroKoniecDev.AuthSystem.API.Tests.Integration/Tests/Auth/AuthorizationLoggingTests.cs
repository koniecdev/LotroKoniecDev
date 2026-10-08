using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using LotroKoniecDev.AuthSystem.API.Middleware;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.Tests.Shared;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// #854: every call the API refuses with 401 or 403 leaves a warning, including a call OpenIddict refuses
/// during authentication. It names the visitor the frontend forwarded with the right key, or else the
/// connection, and then the connection's own address (ADR-0054, amended by #854). Each test boots a
/// derived host whose logger factory captures what the host logged. In Testing <c>UseForwardedHeaders</c>
/// trusts every peer, so <c>X-Forwarded-For</c> plays the connection address Caddy resolves:
/// <c>10.60.0.x</c> is the frontend container, RFC 5737 addresses are visitors. The one 403 here is a
/// client token at an account endpoint (#966). A real service token does not name this API (#1023), so
/// that test gives it this API's audience.
/// </summary>
public sealed class AuthorizationLoggingTests : EndpointsTestBase
{
    // Built rather than written out, so no secret scanner mistakes test data for a key.
    private static readonly string FrontendKey = new('k', 40);

    private const string FrontendAddress = "10.60.0.7";
    private const string ForwardedForHeader = "X-Forwarded-For";
    private const string AccountPath = "auth/account/data-export";
    private const string TokenPath = "connect/token";
    private const string ChangePasswordPath = "auth/change-password";

    public AuthorizationLoggingTests(AuthSystemApiFactory appFactory) : base(appFactory)
    {
    }

    [Fact]
    public async Task GetAccountData_WithoutAToken_ShouldWarnAboutTheUnauthorizedCall()
    {
        // Arrange
        using CapturingLoggerFactory loggerFactory = new();
        using WebApplicationFactory<Program> host = CreateHost(loggerFactory);
        using HttpClient client = host.CreateClient();
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, AccountPath, "203.0.113.90");

        // Act
        using HttpResponseMessage response = await client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        CapturingLoggerFactory.LogEntry warning = MiddlewareEntries(loggerFactory).ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.EventId.Id.ShouldBe(EventIds.UnauthorizedAccessAttempt);
        warning.Message.ShouldBe($"Unauthorized access attempt: GET /{AccountPath} from 203.0.113.90 via 203.0.113.90");
    }

    [Fact]
    public async Task TokenRequest_WithAWrongClientSecret_ShouldWarnAboutTheUnauthorizedCall()
    {
        // Arrange: OpenIddict refuses this during authentication, before authorization runs
        using CapturingLoggerFactory loggerFactory = new();
        using WebApplicationFactory<Program> host = CreateHost(loggerFactory);
        using HttpClient client = host.CreateClient();
        using HttpRequestMessage request = CreateClientCredentialsRequest("DefinitelyWrongSecret1!", "203.0.113.91");

        // Act
        using HttpResponseMessage response = await client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        CapturingLoggerFactory.LogEntry warning = MiddlewareEntries(loggerFactory).ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.EventId.Id.ShouldBe(EventIds.UnauthorizedAccessAttempt);
        warning.Message.ShouldBe($"Unauthorized access attempt: POST /{TokenPath} from 203.0.113.91 via 203.0.113.91");
    }

    [Fact]
    public async Task GetAccountData_WithAClientTokenThatNamesThisApi_ShouldWarnAboutTheForbiddenCall()
    {
        // Arrange: the token comes from this host, because each host signs with its own keys. A real service
        // token does not name this API (#1023), so the host gives it this API's audience, and only the
        // policy is left to refuse it.
        using CapturingLoggerFactory loggerFactory = new();
        using WebApplicationFactory<Program> host = CreateHost(loggerFactory, services =>
            services.OverrideSignInAudiences(
                context => context.Request.IsClientCredentialsGrantType(),
                AuthConstants.ClientIds.Api,
                AuthConstants.Audiences.AuthApi));
        using HttpClient client = host.CreateClient();
        using HttpRequestMessage tokenRequest = CreateClientCredentialsRequest(AuthSystemApiFactory.TestApiClientSecret, "203.0.113.95");
        using HttpResponseMessage tokenResponse = await client.SendAsync(tokenRequest);
        tokenResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument token = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, AccountPath, "203.0.113.95");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", token.RootElement.GetProperty("access_token").GetString());

        // Act
        using HttpResponseMessage response = await client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        CapturingLoggerFactory.LogEntry warning = MiddlewareEntries(loggerFactory).ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.EventId.Id.ShouldBe(EventIds.ForbiddenAccessAttempt);
        warning.Message.ShouldBe(
            $"Forbidden access attempt: GET /{AccountPath} from 203.0.113.95 via 203.0.113.95 by {AuthConstants.ClientIds.Api}");
    }

    [Theory]
    [InlineData(true, "203.0.113.92")]
    [InlineData(false, FrontendAddress)]
    public async Task GetAccountData_WhenRefusedThroughTheFrontend_ShouldNameTheClientAndTheConnection(
        bool keyMatches,
        string namedClient)
    {
        // Arrange: the frontend forwards a visitor. Only the right key makes the forwarded address the
        // client, and the connection's own address is in the line either way.
        using CapturingLoggerFactory loggerFactory = new();
        using WebApplicationFactory<Program> host = CreateHost(loggerFactory);
        using HttpClient client = host.CreateClient();
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, AccountPath, FrontendAddress);
        request.Headers.Add(FrontendCallerHeaders.Key, keyMatches ? FrontendKey : new string('w', 40));
        request.Headers.Add(FrontendCallerHeaders.ClientAddress, "203.0.113.92");

        // Act
        using HttpResponseMessage response = await client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        MiddlewareEntries(loggerFactory).ShouldHaveSingleItem().Message
            .ShouldBe($"Unauthorized access attempt: GET /{AccountPath} from {namedClient} via {FrontendAddress}");
    }

    [Fact]
    public async Task ChangePassword_WithoutAToken_ShouldWarnAlthoughNoRateLimitApplies()
    {
        // Arrange: this endpoint opts out of every per-address limit (ADR-0053). It is a real endpoint,
        // so its refusal is still worth a warning.
        using CapturingLoggerFactory loggerFactory = new();
        using WebApplicationFactory<Program> host = CreateHost(loggerFactory);
        using HttpClient client = host.CreateClient();
        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, ChangePasswordPath, "203.0.113.94");
        request.Content = JsonContent.Create(new { currentPassword = "TestPass1!", newPassword = "NewPass99!" });

        // Act
        using HttpResponseMessage response = await client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        MiddlewareEntries(loggerFactory).ShouldHaveSingleItem().Message
            .ShouldBe($"Unauthorized access attempt: POST /{ChangePasswordPath} from 203.0.113.94 via 203.0.113.94");
    }

    [Fact]
    public async Task TokenRequest_WithTheRightClientSecret_ShouldNotWarn()
    {
        // Arrange
        using CapturingLoggerFactory loggerFactory = new();
        using WebApplicationFactory<Program> host = CreateHost(loggerFactory);
        using HttpClient client = host.CreateClient();
        using HttpRequestMessage request = CreateClientCredentialsRequest(AuthSystemApiFactory.TestApiClientSecret, "203.0.113.93");

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

    private static HttpRequestMessage CreateClientCredentialsRequest(string clientSecret, string connectionAddress)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Post, TokenPath, connectionAddress);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = AuthConstants.ClientIds.Api,
            ["client_secret"] = clientSecret,
            ["scope"] = "api service"
        });
        return request;
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, string connectionAddress)
    {
        HttpRequestMessage request = new(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(ForwardedForHeader, connectionAddress);
        return request;
    }

    private WebApplicationFactory<Program> CreateHost(
        CapturingLoggerFactory loggerFactory,
        Action<IServiceCollection>? configureServices = null)
    {
        return Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "FrontendCaller:Key", FrontendKey }
                });
            });

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<ILoggerFactory>(loggerFactory);
                configureServices?.Invoke(services);
            });
        });
    }
}
