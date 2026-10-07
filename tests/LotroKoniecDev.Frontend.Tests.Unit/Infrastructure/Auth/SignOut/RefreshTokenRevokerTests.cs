using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Web;
using LotroKoniecDev.Frontend.Infrastructure.Auth;
using LotroKoniecDev.Frontend.Infrastructure.Auth.SignOut;
using LotroKoniecDev.Frontend.Infrastructure.Auth.TokenRefresh;
using LotroKoniecDev.Frontend.Settings;
using LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Tests.Unit.Shared;
using LotroKoniecDev.Hateoas.Abstractions;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using NSubstitute;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Auth.SignOut;

/// <summary>
/// #964, and #1027 for more than one token. The discovery document and the auth API are the boundaries:
/// the first is a substitute configuration manager, the second a stub transport under the real token
/// client.
/// </summary>
public sealed class RefreshTokenRevokerTests
{
    // Built rather than written out, so no secret scanner mistakes test data for a key.
    private static readonly string CallerKey = new('k', 40);

    private const string AuthBaseUrl = "https://auth.lotro.test/";
    private const string RevocationEndpoint = "https://auth.lotro.test/connect/revoke";
    private const string RefreshToken = "the-refresh-token";
    private const string VisitorAddress = "203.0.113.7";

    /// <summary>A real-time guard, so a broken time limit fails the test instead of hanging the run.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RevokeAsync_WhenDiscoveryNamesTheRevocationEndpoint_RevokesTheRefreshTokenThere()
    {
        StubHttpMessageHandler transport = StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, string.Empty);
        RefreshTokenRevoker revoker = CreateRevoker(transport, DiscoveryNaming(RevocationEndpoint));

        await revoker.RevokeAsync(RefreshToken);

        transport.LastRequest.ShouldNotBeNull().RequestUri.ShouldBe(new Uri(RevocationEndpoint));
        transport.LastRequestBody.ShouldNotBeNull().ShouldContain($"token={RefreshToken}");
    }

    /// <summary>
    /// #1027: a renewal answer the website throws away carries a new refresh token, and the auth server has
    /// already swapped the stored one for it. Both are still accepted there, so both are revoked.
    /// </summary>
    [Fact]
    public async Task RevokeAsync_WithTwoTokens_RevokesEachOnceAtTheRevocationEndpoint()
    {
        TokenRecordingHttpMessageHandler transport = new();
        RefreshTokenRevoker revoker = CreateRevoker(transport, DiscoveryNaming(RevocationEndpoint));

        await revoker.RevokeAsync("the-stored-refresh-token", "the-received-refresh-token");

        transport.Requests.ShouldBe(
            [
                $"{RevocationEndpoint} token=the-stored-refresh-token",
                $"{RevocationEndpoint} token=the-received-refresh-token"
            ],
            ignoreOrder: true);
    }

    /// <summary>
    /// A caller passes every token it holds. A missing or blank one is no token, and a renewal answer may
    /// repeat the stored token, so each real token is revoked once.
    /// </summary>
    [Fact]
    public async Task RevokeAsync_WithMissingBlankAndRepeatedTokens_RevokesEachRealTokenOnce()
    {
        TokenRecordingHttpMessageHandler transport = new();
        RefreshTokenRevoker revoker = CreateRevoker(transport, DiscoveryNaming(RevocationEndpoint));

        await revoker.RevokeAsync(null, string.Empty, "   ", "\t\r\n", "token-a", "token-a", "token-b");

        transport.Requests.ShouldBe(
            [$"{RevocationEndpoint} token=token-a", $"{RevocationEndpoint} token=token-b"],
            ignoreOrder: true);
    }

    /// <summary>
    /// A session with no refresh token has nothing to revoke. Even with the auth server down it must not
    /// log a failed revoke, or every such session end would look like one.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public async Task RevokeAsync_WithNoRealTokenWhileDiscoveryIsDown_LogsNothing(string? blankToken)
    {
        IConfigurationManager<OpenIdConnectConfiguration> configurationManager =
            Substitute.For<IConfigurationManager<OpenIdConnectConfiguration>>();
        configurationManager.GetConfigurationAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<OpenIdConnectConfiguration>(new HttpRequestException("Connection refused.")));
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);
        RefreshTokenRevoker revoker = CreateRevoker(
            new TokenRecordingHttpMessageHandler(), configurationManager, loggerFactory: loggerFactory);

        await revoker.RevokeAsync(blankToken);

        logs.Entries.ShouldBeEmpty();
    }

    /// <summary>
    /// A token client that throws before it returns a task, as a decorator might, must not stop the other
    /// token's revoke either. The revoke is not visible in the result, hence the check on the substitute.
    /// </summary>
    [Fact]
    public async Task RevokeAsync_WhenTheClientThrowsBeforeReturningATaskForOneToken_StillRevokesTheOther()
    {
        ITokenEndpointClient tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
        tokenEndpointClient
            .RevokeRefreshTokenAsync(Arg.Any<Uri>(), "token-a", Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("A decorator failed."));
        RefreshTokenRevoker revoker = new(
            tokenEndpointClient,
            OidcOptionsWith(DiscoveryNaming(RevocationEndpoint)),
            TimeProvider.System,
            NullLogger<RefreshTokenRevoker>.Instance);

        await revoker.RevokeAsync("token-a", "token-b");

        await tokenEndpointClient.Received(1)
            .RevokeRefreshTokenAsync(new Uri(RevocationEndpoint), "token-b", Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The token client catches only the failures it expects. One revoke that fails in another way must not
    /// stop the other token from being revoked.
    /// </summary>
    [Fact]
    public async Task RevokeAsync_WhenOneOfTwoRevokesThrowsAnUnexpectedException_StillRevokesTheOther()
    {
        TokenRecordingHttpMessageHandler transport = new(failingToken: "token-a");
        RefreshTokenRevoker revoker = CreateRevoker(transport, DiscoveryNaming(RevocationEndpoint));

        await revoker.RevokeAsync("token-a", "token-b");

        transport.Requests.ShouldBe([$"{RevocationEndpoint} token=token-b"]);
    }

    public static TheoryData<Exception> DiscoveryFailures() => new()
    {
        new InvalidOperationException("IDX20803: Unable to obtain configuration from the authority."),
        new HttpRequestException("Connection refused."),
        new TaskCanceledException("The discovery request timed out.")
    };

    [Theory]
    [MemberData(nameof(DiscoveryFailures))]
    public async Task RevokeAsync_WhenDiscoveryCannotBeRead_LogsOneWarningAndSendsNothing(Exception failure)
    {
        IConfigurationManager<OpenIdConnectConfiguration> configurationManager =
            Substitute.For<IConfigurationManager<OpenIdConnectConfiguration>>();
        configurationManager.GetConfigurationAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<OpenIdConnectConfiguration>(failure));
        StubHttpMessageHandler transport = StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, string.Empty);
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);
        RefreshTokenRevoker revoker = CreateRevoker(transport, configurationManager, loggerFactory: loggerFactory);

        await revoker.RevokeAsync(RefreshToken);

        transport.LastRequest.ShouldBeNull();
        CapturingLoggerProvider.LogEntry entry = logs.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldBe(
            "The auth server's discovery document could not be read, so no refresh token was revoked.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("connect/revoke")]
    [InlineData("/connect/revoke")]
    [InlineData("file:///connect/revoke")]
    [InlineData("ftp://auth.lotro.test/connect/revoke")]
    public async Task RevokeAsync_WhenDiscoveryNamesNoAbsoluteWebRevocationEndpoint_LogsOneWarningAndSendsNothing(
        string? revocationEndpoint)
    {
        StubHttpMessageHandler transport = StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, string.Empty);
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);
        RefreshTokenRevoker revoker = CreateRevoker(
            transport, DiscoveryNaming(revocationEndpoint), loggerFactory: loggerFactory);

        await revoker.RevokeAsync(RefreshToken);

        transport.LastRequest.ShouldBeNull();
        CapturingLoggerProvider.LogEntry entry = logs.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldBe(
            "The auth server's discovery document has no usable revocation endpoint, so no refresh token was revoked.");
    }

    [Fact]
    public async Task RevokeAsync_WhenThereIsNoConfigurationManager_LogsOneWarningAndSendsNothing()
    {
        StubHttpMessageHandler transport = StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, string.Empty);
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);
        RefreshTokenRevoker revoker = CreateRevoker(transport, configurationManager: null, loggerFactory: loggerFactory);

        await revoker.RevokeAsync(RefreshToken);

        transport.LastRequest.ShouldBeNull();
        CapturingLoggerProvider.LogEntry entry = logs.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldBe(
            "The OIDC handler has no configuration manager, so no refresh token was revoked.");
    }

    /// <summary>
    /// The token client catches only the failures it expects. Anything else must still not escape,
    /// because an escape after the cookie sign-out would leave the user signed in.
    /// </summary>
    [Fact]
    public async Task RevokeAsync_WhenTheRevokeThrowsAnUnexpectedException_LogsOneWarningAndDoesNotThrow()
    {
        StubHttpMessageHandler transport = StubHttpMessageHandler.Throw(new InvalidOperationException("A new handler failed."));
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);
        RefreshTokenRevoker revoker = CreateRevoker(
            transport, DiscoveryNaming(RevocationEndpoint), loggerFactory: loggerFactory);

        await revoker.RevokeAsync(RefreshToken);

        CapturingLoggerProvider.LogEntry entry = logs.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldBe("Refresh token revocation failed with an unexpected exception.");
    }

    [Fact]
    public async Task RevokeAsync_WhenTheOidcOptionsCannotBeBuilt_LogsOneWarningAndDoesNotThrow()
    {
        IOptionsMonitor<OpenIdConnectOptions> optionsMonitor = Substitute.For<IOptionsMonitor<OpenIdConnectOptions>>();
        optionsMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme)
            .Returns(_ => throw new OptionsValidationException(
                OpenIdConnectDefaults.AuthenticationScheme, typeof(OpenIdConnectOptions), ["The options are invalid."]));
        StubHttpMessageHandler transport = StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, string.Empty);
        using HttpClient httpClient = new(transport) { BaseAddress = new Uri(AuthBaseUrl) };
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);
        RefreshTokenRevoker revoker = new(
            new TokenEndpointClient(
                httpClient,
                Microsoft.Extensions.Options.Options.Create(CreateSettings()),
                loggerFactory.CreateLogger<TokenEndpointClient>()),
            optionsMonitor,
            TimeProvider.System,
            loggerFactory.CreateLogger<RefreshTokenRevoker>());

        await revoker.RevokeAsync(RefreshToken);

        transport.LastRequest.ShouldBeNull();
        logs.Entries.ShouldHaveSingleItem().Message
            .ShouldBe("Refresh token revocation failed with an unexpected exception.");
    }

    /// <summary>
    /// The sign-out waits for the revoke, so a discovery fetch that never answers must not hold it past
    /// the limit, even if the configuration manager ignores the token.
    /// </summary>
    [Fact]
    public async Task RevokeAsync_WhenDiscoveryNeverAnswers_GivesUpExactlyAtTheTimeLimit()
    {
        IConfigurationManager<OpenIdConnectConfiguration> configurationManager =
            Substitute.For<IConfigurationManager<OpenIdConnectConfiguration>>();
        configurationManager.GetConfigurationAsync(Arg.Any<CancellationToken>())
            .Returns(new TaskCompletionSource<OpenIdConnectConfiguration>().Task);
        StubHttpMessageHandler transport = StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, string.Empty);
        FakeTimeProvider time = new();
        RefreshTokenRevoker revoker = CreateRevoker(transport, configurationManager, time);

        Task revoke = revoker.RevokeAsync(RefreshToken);
        time.Advance(RefreshTokenRevoker.TimeLimit - TimeSpan.FromTicks(1));
        bool finishedBeforeTheLimit = revoke.IsCompleted;
        time.Advance(TimeSpan.FromTicks(1));
        await revoke.WaitAsync(TestTimeout);

        finishedBeforeTheLimit.ShouldBeFalse();
        transport.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task RevokeAsync_WhenTheAuthApiNeverAnswers_GivesUpAtTheTimeLimitAndLogsOneWarning()
    {
        HangingHttpMessageHandler transport = new();
        FakeTimeProvider time = new();
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);
        RefreshTokenRevoker revoker = CreateRevoker(transport, DiscoveryNaming(RevocationEndpoint), time, loggerFactory);

        Task revoke = revoker.RevokeAsync(RefreshToken);
        await transport.AllRequestsStarted.Task.WaitAsync(TestTimeout);
        time.Advance(RefreshTokenRevoker.TimeLimit);
        await revoke.WaitAsync(TestTimeout);

        CapturingLoggerProvider.LogEntry entry = logs.Entries.ShouldHaveSingleItem();
        entry.Message.ShouldBe("Refresh token revocation threw an exception.");
    }

    /// <summary>
    /// #1027: two tokens never hold the request longer than one. Both revokes are sent at once and share
    /// the one time limit, so neither waits for the other.
    /// </summary>
    [Fact]
    public async Task RevokeAsync_WhenTheAuthApiNeverAnswersEitherOfTwoRevokes_GivesUpOnBothAtOneTimeLimit()
    {
        HangingHttpMessageHandler transport = new(expectedRequests: 2);
        FakeTimeProvider time = new();
        RefreshTokenRevoker revoker = CreateRevoker(transport, DiscoveryNaming(RevocationEndpoint), time);

        Task revoke = revoker.RevokeAsync("the-stored-refresh-token", "the-received-refresh-token");
        await transport.AllRequestsStarted.Task.WaitAsync(TestTimeout);
        time.Advance(RefreshTokenRevoker.TimeLimit - TimeSpan.FromTicks(1));
        bool finishedBeforeTheLimit = revoke.IsCompleted;
        time.Advance(TimeSpan.FromTicks(1));
        await revoke.WaitAsync(TestTimeout);

        finishedBeforeTheLimit.ShouldBeFalse();
    }

    /// <summary>
    /// The auth API meters connect/revoke per visitor (ADR-0054), so the revoke carries the visitor's
    /// address and the caller key like every other call to it.
    /// </summary>
    [Fact]
    public async Task RevokeAsync_ThroughTheRealRegistration_SendsTheCallerKeyAndTheVisitorsAddress()
    {
        RecordingHttpMessageHandler primary = new();
        await using ServiceProvider provider = BuildRealRegistration(primary, DiscoveryNaming(RevocationEndpoint));
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        RefreshTokenRevoker revoker = scope.ServiceProvider.GetRequiredService<RefreshTokenRevoker>();

        await revoker.RevokeAsync(RefreshToken);

        primary.SendCount.ShouldBe(1);
        primary.LastCallerKey.ShouldBe(CallerKey);
        primary.LastClientAddress.ShouldBe(VisitorAddress);
    }

    /// <summary>
    /// #830: the endpoint is a link from another service, so a link off the auth origin must never carry
    /// the refresh token or the caller key away.
    /// </summary>
    [Theory]
    [InlineData("https://attacker.example/connect/revoke")]
    [InlineData("https://auth.lotro.test.attacker.example/connect/revoke")]
    [InlineData("http://auth.lotro.test/connect/revoke")]
    [InlineData("https://auth.lotro.test:8443/connect/revoke")]
    public async Task RevokeAsync_ThroughTheRealRegistration_RefusesARevocationEndpointOffTheAuthOrigin(
        string revocationEndpoint)
    {
        RecordingHttpMessageHandler primary = new();
        await using ServiceProvider provider = BuildRealRegistration(primary, DiscoveryNaming(revocationEndpoint));
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        RefreshTokenRevoker revoker = scope.ServiceProvider.GetRequiredService<RefreshTokenRevoker>();

        await revoker.RevokeAsync(RefreshToken);

        primary.SendCount.ShouldBe(0);
    }

    private static IConfigurationManager<OpenIdConnectConfiguration> DiscoveryNaming(string? revocationEndpoint)
    {
        IConfigurationManager<OpenIdConnectConfiguration> configurationManager =
            Substitute.For<IConfigurationManager<OpenIdConnectConfiguration>>();
        configurationManager.GetConfigurationAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new OpenIdConnectConfiguration { RevocationEndpoint = revocationEndpoint }));
        return configurationManager;
    }

    private static RefreshTokenRevoker CreateRevoker(
        HttpMessageHandler transport,
        IConfigurationManager<OpenIdConnectConfiguration>? configurationManager,
        TimeProvider? timeProvider = null,
        ILoggerFactory? loggerFactory = null)
    {
        ILoggerFactory factory = loggerFactory ?? NullLoggerFactory.Instance;
        TokenEndpointClient tokenEndpointClient = new(
            new HttpClient(transport) { BaseAddress = new Uri(AuthBaseUrl) },
            Microsoft.Extensions.Options.Options.Create(CreateSettings()),
            factory.CreateLogger<TokenEndpointClient>());

        return new RefreshTokenRevoker(
            tokenEndpointClient,
            OidcOptionsWith(configurationManager),
            timeProvider ?? TimeProvider.System,
            factory.CreateLogger<RefreshTokenRevoker>());
    }

    private static IOptionsMonitor<OpenIdConnectOptions> OidcOptionsWith(
        IConfigurationManager<OpenIdConnectConfiguration>? configurationManager)
    {
        IOptionsMonitor<OpenIdConnectOptions> optionsMonitor = Substitute.For<IOptionsMonitor<OpenIdConnectOptions>>();
        optionsMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme)
            .Returns(new OpenIdConnectOptions { ConfigurationManager = configurationManager });
        return optionsMonitor;
    }

    /// <summary>
    /// The frontend's own registration with the socket handler swapped for a recording stub and the
    /// discovery document swapped for the given one.
    /// </summary>
    private static ServiceProvider BuildRealRegistration(
        HttpMessageHandler primary,
        IConfigurationManager<OpenIdConnectConfiguration> configurationManager)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton(Visitor());
        services.AddSingleton<IOptions<AuthSystemSettings>>(Microsoft.Extensions.Options.Options.Create(CreateSettings()));
        services.AddFrontendAuthentication();
        services.AddHttpClient<ITokenEndpointClient, TokenEndpointClient>()
            .ConfigurePrimaryHttpMessageHandler(() => primary);
        services.PostConfigure<OpenIdConnectOptions>(
            OpenIdConnectDefaults.AuthenticationScheme,
            options => options.ConfigurationManager = configurationManager);

        return services.BuildServiceProvider();
    }

    private static AuthSystemSettings CreateSettings() => new()
    {
        BaseUrl = AuthBaseUrl,
        Authority = "https://auth.lotro.test",
        ClientId = "lotrokoniecdev-web",
        CallbackPath = "/callback",
        SignedOutCallbackPath = "/signout-callback-oidc",
        Scopes = ["openid", "email", "profile"],
        CallerKey = CallerKey
    };

    private static IHttpContextAccessor Visitor()
    {
        DefaultHttpContext httpContext = new();
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse(VisitorAddress);

        IHttpContextAccessor accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(httpContext);
        return accessor;
    }

    private sealed class HangingHttpMessageHandler : HttpMessageHandler
    {
        private readonly int _expectedRequests;
        private int _startedRequests;

        public HangingHttpMessageHandler(int expectedRequests = 1)
        {
            _expectedRequests = expectedRequests;
        }

        /// <summary>Completes once the expected number of requests are all waiting at the same time.</summary>
        public TaskCompletionSource AllRequestsStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _startedRequests) == _expectedRequests)
            {
                AllRequestsStarted.TrySetResult();
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        }
    }

    /// <summary>
    /// Records every call as "address token" and answers each with 200. Calls may arrive at once. A call
    /// for the failing token throws an exception the token client does not expect, and is not recorded.
    /// </summary>
    private sealed class TokenRecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> _requests = new();
        private readonly string? _failingToken;

        public TokenRecordingHttpMessageHandler(string? failingToken = null)
        {
            _failingToken = failingToken;
        }

        public IReadOnlyCollection<string> Requests => _requests;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            string token = HttpUtility.ParseQueryString(body)["token"] ?? string.Empty;
            if (token == _failingToken)
            {
                throw new InvalidOperationException("A new handler failed.");
            }

            _requests.Enqueue($"{request.RequestUri} token={token}");
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private int _sendCount;

        public int SendCount => _sendCount;

        public string? LastCallerKey { get; private set; }

        public string? LastClientAddress { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sendCount);
            LastCallerKey = HeaderValue(request, FrontendCallerHeaders.Key);
            LastClientAddress = HeaderValue(request, FrontendCallerHeaders.ClientAddress);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }

        private static string? HeaderValue(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out IEnumerable<string>? values) ? values.Single() : null;
    }
}
