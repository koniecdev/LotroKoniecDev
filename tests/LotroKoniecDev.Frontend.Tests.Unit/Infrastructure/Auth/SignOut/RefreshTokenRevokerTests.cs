using System.Diagnostics;
using System.Net;
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
/// #964. The discovery document and the auth API are the boundaries: the first is a substitute
/// configuration manager, the second a stub transport under the real token client.
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
            "The auth server's discovery document could not be read at sign-out, so the refresh token was not revoked.");
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
            "The auth server's discovery document has no usable revocation endpoint, so the refresh token was not revoked at sign-out.");
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
            "The OIDC handler has no configuration manager, so the refresh token was not revoked at sign-out.");
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
        entry.Message.ShouldBe("Refresh token revocation at sign-out failed with an unexpected exception.");
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
            .ShouldBe("Refresh token revocation at sign-out failed with an unexpected exception.");
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
        await transport.RequestStarted.Task.WaitAsync(TestTimeout);
        time.Advance(RefreshTokenRevoker.TimeLimit);
        await revoke.WaitAsync(TestTimeout);

        CapturingLoggerProvider.LogEntry entry = logs.Entries.ShouldHaveSingleItem();
        entry.Message.ShouldBe("Refresh token revocation at sign-out threw an exception.");
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

        IOptionsMonitor<OpenIdConnectOptions> optionsMonitor = Substitute.For<IOptionsMonitor<OpenIdConnectOptions>>();
        optionsMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme)
            .Returns(new OpenIdConnectOptions { ConfigurationManager = configurationManager });

        return new RefreshTokenRevoker(
            tokenEndpointClient,
            optionsMonitor,
            timeProvider ?? TimeProvider.System,
            factory.CreateLogger<RefreshTokenRevoker>());
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
        public TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
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
