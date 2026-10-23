using System.Globalization;
using System.Security.Claims;
using LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;
using LotroKoniecDev.Frontend.Infrastructure.Auth.SignOut;
using LotroKoniecDev.Frontend.Infrastructure.Auth.TokenRefresh;
using LotroKoniecDev.Frontend.Tests.Unit.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using NSubstitute;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Auth.TokenRefresh;

public sealed class CookieTokenRefresherTests
{
    private const string RevocationEndpoint = "https://localhost:5003/connect/revoke";
    private const string Subject = "11111111-1111-1111-1111-111111111111";
    private const string AccessTokenName = "access_token";
    private const string RefreshTokenName = "refresh_token";
    private const string IdTokenName = "id_token";
    private const string ExpiresAtName = "expires_at";
    private const string RefreshAtName = "refresh_at";
    private const string FarFutureExpiresAt = "2999-01-01T00:00:00.0000000+00:00";
    private const string RefreshedAccessToken = "refreshed-access-token";
    private const string RotatedIdToken = "rotated-id-token";

    /// <summary>A real-time guard, so a broken time limit fails the test instead of hanging the run.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private static readonly DateTimeOffset Start = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ValidateAsync_WithAnOpaqueAccessTokenFarFromExpiry_KeepsPrincipalWithoutCallingTheSignInServer()
    {
        ITokenEndpointClient tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
        CookieTokenRefresher refresher = CreateRefresher(tokenEndpointClient: tokenEndpointClient);
        CookieValidatePrincipalContext context = CreateContext(
            "opaque-access-token",
            Substitute.For<IAuthenticationService>(),
            expiresAt: DateTimeOffset.UtcNow.AddHours(1),
            refreshToken: "refresh-token");

        await refresher.ValidateAsync(context);

        context.Principal.ShouldNotBeNull();
        tokenEndpointClient.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task ValidateAsync_WhenNearExpiryWithoutRefreshToken_RejectsPrincipal()
    {
        string accessToken = "stored-access-token";

        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        CookieTokenRefresher refresher = CreateRefresher();
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            authenticationService,
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(30),
            refreshToken: null);

        await refresher.ValidateAsync(context);

        context.Principal.ShouldBeNull();
        await authenticationService.Received(1).SignOutAsync(
            Arg.Any<HttpContext>(),
            CookieAuthenticationDefaults.AuthenticationScheme,
            Arg.Any<AuthenticationProperties>());
    }

    [Fact]
    public async Task ValidateAsync_WhenNearExpiryAndTheRefreshIsRefused_RejectsPrincipalAndSignsOut()
    {
        // A final failure: any refusal but a 429 or a 5xx, a redirect included (#899), or a whole answer
        // that cannot be read (ADR-0067 decision 3).
        string accessToken = "stored-access-token";

        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        CookieTokenRefresher refresher = CreateRefresher(refreshFailure: TokenRefreshResult.FinalFailure);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            authenticationService,
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(30),
            refreshToken: "refresh-token");

        await refresher.ValidateAsync(context);

        context.Principal.ShouldBeNull();
        await authenticationService.Received(1).SignOutAsync(
            Arg.Any<HttpContext>(),
            CookieAuthenticationDefaults.AuthenticationScheme,
            Arg.Any<AuthenticationProperties>());
    }

    /// <summary>
    /// #1062: the cookie is not rewritten, so the refresh stays due.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_WhenTheRefreshFailsForAMomentWhileTheAccessTokenWorks_KeepsTheSessionAsItIs()
    {
        string accessToken = "stored-access-token";

        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        CookieTokenRefresher refresher = CreateRefresher(refreshFailure: TokenRefreshResult.TemporaryFailure);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            authenticationService,
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(30),
            refreshToken: "refresh-token");

        await refresher.ValidateAsync(context);

        context.Principal.ShouldNotBeNull();
        context.ShouldRenew.ShouldBeFalse();
        context.Properties.GetTokenValue(AccessTokenName).ShouldBe(accessToken);
        context.Properties.GetTokenValue(RefreshTokenName).ShouldBe("refresh-token");
        await authenticationService.DidNotReceive().SignOutAsync(
            Arg.Any<HttpContext>(),
            Arg.Any<string>(),
            Arg.Any<AuthenticationProperties>());
    }

    [Fact]
    public async Task ValidateAsync_WhenTheRefreshFailsForAMomentWhileTheAccessTokenWorks_RevokesNothing()
    {
        string accessToken = "stored-access-token";

        ITokenEndpointClient tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
        CookieTokenRefresher refresher = CreateRefresher(
            tokenEndpointClient: tokenEndpointClient,
            refreshFailure: TokenRefreshResult.TemporaryFailure);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(30),
            refreshToken: "refresh-token");

        await refresher.ValidateAsync(context);

        RevokedRefreshTokens(tokenEndpointClient).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false, 30)]
    [InlineData(true, -1)]
    public async Task ValidateAsync_WhenTheRefreshFailsForGood_RaisesTheExpiryNotice(
        bool temporaryFailure,
        int secondsUntilExpiry)
    {
        string accessToken = "stored-access-token";

        ISessionExpiryNotice sessionExpiryNotice = Substitute.For<ISessionExpiryNotice>();
        CookieTokenRefresher refresher = CreateRefresher(
            sessionExpiryNotice: sessionExpiryNotice,
            refreshFailure: temporaryFailure ? TokenRefreshResult.TemporaryFailure : TokenRefreshResult.FinalFailure);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(secondsUntilExpiry),
            refreshToken: "refresh-token");

        await refresher.ValidateAsync(context);

        sessionExpiryNotice.Received(1).Raise();
    }

    [Fact]
    public async Task ValidateAsync_WhenTheRefreshFailsForAMomentWhileTheAccessTokenWorks_RaisesNoExpiryNotice()
    {
        string accessToken = "stored-access-token";

        ISessionExpiryNotice sessionExpiryNotice = Substitute.For<ISessionExpiryNotice>();
        CookieTokenRefresher refresher = CreateRefresher(
            sessionExpiryNotice: sessionExpiryNotice,
            refreshFailure: TokenRefreshResult.TemporaryFailure);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(30),
            refreshToken: "refresh-token");

        await refresher.ValidateAsync(context);

        sessionExpiryNotice.DidNotReceive().Raise();
    }

    [Fact]
    public async Task ValidateAsync_OnTheNextPageAfterARefreshFailedForAMoment_TriesAgainAndStoresTheNewTokens()
    {
        string accessToken = "stored-access-token";
        FakeTimeProvider time = new(Start);

        ITokenEndpointClient tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
        tokenEndpointClient
            .RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                TokenRefreshResult.TemporaryFailure,
                TokenRefreshResult.Renewed(new TokenResponse
                {
                    AccessToken = RefreshedAccessToken,
                    RefreshToken = "rotated-refresh-token",
                    ExpiresIn = 300
                }));
        CookieTokenRefresher refresher = CreateRefresher(
            tokenEndpointClient: tokenEndpointClient,
            timeProvider: time);
        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        CookieValidatePrincipalContext firstPage = CreateContext(
            accessToken,
            authenticationService,
            expiresAt: Start.AddSeconds(30),
            refreshToken: "refresh-token");
        await refresher.ValidateAsync(firstPage);

        time.Advance(TimeSpan.FromSeconds(5));
        CookieValidatePrincipalContext nextPage = CreateContext(firstPage.Properties, authenticationService);
        await refresher.ValidateAsync(nextPage);

        nextPage.Principal.ShouldNotBeNull();
        nextPage.ShouldRenew.ShouldBeTrue();
        nextPage.Properties.GetTokenValue(AccessTokenName).ShouldBe(RefreshedAccessToken);
        nextPage.Properties.GetTokenValue(RefreshTokenName).ShouldBe("rotated-refresh-token");
    }

    /// <summary>
    /// #1062: once the access token has run out, a temporary failure is final. The call to the sign-in
    /// server is checked too, because a session never due for a refresh would also be kept.
    /// </summary>
    [Theory]
    [InlineData(59, true)]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(-3600, false)]
    public async Task ValidateAsync_WhenTheRefreshFailsForAMoment_KeepsTheSessionOnlyWhileTheAccessTokenWorks(
        int secondsUntilExpiry,
        bool sessionKept)
    {
        string accessToken = "stored-access-token";
        FakeTimeProvider time = new(Start);

        ITokenEndpointClient tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
        CookieTokenRefresher refresher = CreateRefresher(
            tokenEndpointClient: tokenEndpointClient,
            timeProvider: time,
            refreshFailure: TokenRefreshResult.TemporaryFailure);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: Start.AddSeconds(secondsUntilExpiry),
            refreshToken: "refresh-token");

        await refresher.ValidateAsync(context);

        (context.Principal is not null).ShouldBe(sessionKept);
        await tokenEndpointClient.Received(1).RefreshAsync("refresh-token", Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A timeout takes 30 seconds (#923), so the access token can run out while the website waits.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_WhenATemporaryFailureArrivesAfterTheAccessTokenRanOut_RejectsPrincipalAndSignsOut()
    {
        string accessToken = "stored-access-token";
        FakeTimeProvider time = new(Start);

        ITokenEndpointClient tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
        tokenEndpointClient
            .RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                time.Advance(TimeSpan.FromSeconds(30));
                return TokenRefreshResult.TemporaryFailure;
            });
        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        CookieTokenRefresher refresher = CreateRefresher(
            tokenEndpointClient: tokenEndpointClient,
            timeProvider: time);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            authenticationService,
            expiresAt: Start.AddSeconds(20),
            refreshToken: "refresh-token");

        await refresher.ValidateAsync(context);

        context.Principal.ShouldBeNull();
        await authenticationService.Received(1).SignOutAsync(
            Arg.Any<HttpContext>(),
            CookieAuthenticationDefaults.AuthenticationScheme,
            Arg.Any<AuthenticationProperties>());
    }

    [Fact]
    public async Task ValidateAsync_WhenTheRefreshFailsForAMomentAfterTheAccessTokenRanOut_RevokesTheStoredRefreshToken()
    {
        string accessToken = "stored-access-token";

        ITokenEndpointClient tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
        CookieTokenRefresher refresher = CreateRefresher(
            tokenEndpointClient: tokenEndpointClient,
            refreshFailure: TokenRefreshResult.TemporaryFailure);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1),
            refreshToken: "refresh-token");

        await refresher.ValidateAsync(context);

        RevokedRefreshTokens(tokenEndpointClient).ShouldBe(["refresh-token"]);
    }

    /// <summary>
    /// #977: a temporary failure is normal, so it is Information. The token client already logged the cause
    /// as a warning.
    /// </summary>
    [Theory]
    [InlineData(30, "the session keeps its access token and the next request tries again")]
    [InlineData(-1, "after the access token ran out; principal rejected")]
    public async Task ValidateAsync_WhenTheRefreshFailsForAMoment_LogsOneInformationLineWithTheOutcome(
        int secondsUntilExpiry,
        string expectedOutcome)
    {
        string accessToken = "stored-access-token";
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);

        CookieTokenRefresher refresher = CreateRefresher(
            logger: loggerFactory.CreateLogger<CookieTokenRefresher>(),
            refreshFailure: TokenRefreshResult.TemporaryFailure);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(secondsUntilExpiry),
            refreshToken: "refresh-token");

        await refresher.ValidateAsync(context);

        CapturingLoggerProvider.LogEntry entry = logs.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Information);
        entry.Message.ShouldContain(expectedOutcome);
    }

    /// <summary>
    /// The token client cannot tell a browser that left from a timeout, so a cancelled refresh is a
    /// temporary failure too.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_WhenTheBrowserLeavesDuringTheRefreshWhileTheAccessTokenWorks_KeepsTheSession()
    {
        string accessToken = "stored-access-token";

        CookieTokenRefresher refresher = CreateRefresher(refreshFailure: TokenRefreshResult.TemporaryFailure);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(30),
            refreshToken: "refresh-token",
            requestAborted: new CancellationToken(canceled: true));

        await refresher.ValidateAsync(context);

        context.Principal.ShouldNotBeNull();
    }

    /// <summary>
    /// #974: the API refuses a blank token on every call. None of the answer's other new tokens may be
    /// stored either.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public async Task ValidateAsync_WhenRefreshAnswersWithoutAUsableAccessToken_RejectsPrincipalAndStoresNothing(
        string? blankAccessToken)
    {
        string accessToken = "stored-access-token";
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.AddSeconds(30);

        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        CookieTokenRefresher refresher = CreateRefresher(
            refreshResult: new TokenResponse
            {
                AccessToken = blankAccessToken,
                RefreshToken = "rotated-refresh-token",
                IdToken = RotatedIdToken,
                ExpiresIn = 300
            });
        CookieValidatePrincipalContext context = CreateContext(
            accessToken, authenticationService, expiresAt, refreshToken: "refresh-token", idToken: "id-token");

        await refresher.ValidateAsync(context);

        context.Principal.ShouldBeNull();
        context.ShouldRenew.ShouldBeFalse();
        context.Properties.GetTokenValue(AccessTokenName).ShouldBe(accessToken);
        context.Properties.GetTokenValue(RefreshTokenName).ShouldBe("refresh-token");
        context.Properties.GetTokenValue(IdTokenName).ShouldBe("id-token");
        context.Properties.GetTokenValue(ExpiresAtName).ShouldBe(expiresAt.ToString("o", CultureInfo.InvariantCulture));
        await authenticationService.Received(1).SignOutAsync(
            Arg.Any<HttpContext>(),
            CookieAuthenticationDefaults.AuthenticationScheme,
            Arg.Any<AuthenticationProperties>());
    }

    /// <summary>
    /// #974: a token with no lifetime would look expired at once, so every page would redeem the refresh
    /// token again.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task ValidateAsync_WhenRefreshAnswersWithoutAPositiveLifetime_RejectsPrincipalAndStoresNothing(
        int? expiresIn)
    {
        string accessToken = "stored-access-token";
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.AddSeconds(30);

        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        CookieTokenRefresher refresher = CreateRefresher(
            refreshResult: new TokenResponse
            {
                AccessToken = RefreshedAccessToken,
                RefreshToken = "rotated-refresh-token",
                IdToken = RotatedIdToken,
                ExpiresIn = expiresIn
            });
        CookieValidatePrincipalContext context = CreateContext(
            accessToken, authenticationService, expiresAt, refreshToken: "refresh-token", idToken: "id-token");

        await refresher.ValidateAsync(context);

        context.Principal.ShouldBeNull();
        context.ShouldRenew.ShouldBeFalse();
        context.Properties.GetTokenValue(AccessTokenName).ShouldBe(accessToken);
        context.Properties.GetTokenValue(RefreshTokenName).ShouldBe("refresh-token");
        context.Properties.GetTokenValue(IdTokenName).ShouldBe("id-token");
        context.Properties.GetTokenValue(ExpiresAtName).ShouldBe(expiresAt.ToString("o", CultureInfo.InvariantCulture));
        await authenticationService.Received(1).SignOutAsync(
            Arg.Any<HttpContext>(),
            CookieAuthenticationDefaults.AuthenticationScheme,
            Arg.Any<AuthenticationProperties>());
    }

    /// <summary>
    /// ADR-0067 decision 2: the tokens are opaque to the website, whatever they look like: an encrypted
    /// token, a reference token, or text a request header cannot carry.
    /// </summary>
    [Theory]
    [InlineData("aGVhZGVy.a2V5.aXY.Y2lwaGVy.dGFn", RotatedIdToken, "rotated-refresh-token")]
    [InlineData("opaque-access-token", "null", "rotated-refresh-tökén")]
    [InlineData(RefreshedAccessToken + "\n", RotatedIdToken + "\r\n", "rotated-refresh-token\n")]
    public async Task ValidateAsync_WhenRefreshAnswersWithTokensOfAnyShape_StoresThemAsTheyAre(
        string refreshedAccessToken,
        string refreshedIdToken,
        string refreshedRefreshToken)
    {
        string accessToken = "stored-access-token";

        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        CookieTokenRefresher refresher = CreateRefresher(
            refreshResult: new TokenResponse
            {
                AccessToken = refreshedAccessToken,
                RefreshToken = refreshedRefreshToken,
                IdToken = refreshedIdToken,
                ExpiresIn = 300
            });
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            authenticationService,
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(30),
            refreshToken: "refresh-token",
            idToken: "id-token");

        await refresher.ValidateAsync(context);

        context.Principal.ShouldNotBeNull();
        context.ShouldRenew.ShouldBeTrue();
        context.Properties.GetTokenValue(AccessTokenName).ShouldBe(refreshedAccessToken);
        context.Properties.GetTokenValue(IdTokenName).ShouldBe(refreshedIdToken);
        context.Properties.GetTokenValue(RefreshTokenName).ShouldBe(refreshedRefreshToken);
    }

    /// <summary>
    /// The log is the only place that says why. A warning, because our own sign-in server never sends such
    /// an answer.
    /// </summary>
    [Theory]
    [InlineData(null, 300, "missing, empty or blank access_token")]
    [InlineData("", 300, "missing, empty or blank access_token")]
    [InlineData("   ", 300, "missing, empty or blank access_token")]
    [InlineData(RefreshedAccessToken, null, "expires_in: missing")]
    [InlineData(RefreshedAccessToken, 0, "expires_in: 0")]
    [InlineData(RefreshedAccessToken, -5, "expires_in: -5")]
    public async Task ValidateAsync_WhenRefreshAnswerIsUnusable_LogsOneWarningWithTheReason(
        string? refreshedAccessToken,
        int? expiresIn,
        string expectedReason)
    {
        string accessToken = "stored-access-token";
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);

        CookieTokenRefresher refresher = CreateRefresher(
            refreshResult: new TokenResponse { AccessToken = refreshedAccessToken, ExpiresIn = expiresIn },
            logger: loggerFactory.CreateLogger<CookieTokenRefresher>());
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(30),
            refreshToken: "refresh-token");

        await refresher.ValidateAsync(context);

        CapturingLoggerProvider.LogEntry entry = logs.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldContain(expectedReason);
    }

    /// <summary>
    /// The notice is not visible in the context, hence the check on the substitute.
    /// </summary>
    [Theory]
    [InlineData(null, 300)]
    [InlineData("   ", 300)]
    [InlineData(RefreshedAccessToken, null)]
    [InlineData(RefreshedAccessToken, 0)]
    public async Task ValidateAsync_WhenRefreshAnswerIsUnusable_RaisesTheExpiryNotice(
        string? refreshedAccessToken,
        int? expiresIn)
    {
        string accessToken = "stored-access-token";

        ISessionExpiryNotice sessionExpiryNotice = Substitute.For<ISessionExpiryNotice>();
        CookieTokenRefresher refresher = CreateRefresher(
            refreshResult: new TokenResponse { AccessToken = refreshedAccessToken, ExpiresIn = expiresIn },
            sessionExpiryNotice: sessionExpiryNotice);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(30),
            refreshToken: "refresh-token");

        await refresher.ValidateAsync(context);

        sessionExpiryNotice.Received(1).Raise();
    }

    /// <summary>
    /// #974: a blank refresh token would fail the next refresh, and a blank ID token would leave the
    /// sign-out without its hint.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public async Task ValidateAsync_WhenRefreshAnswersWithABlankRefreshOrIdToken_KeepsTheStoredOnes(
        string? blankToken)
    {
        string accessToken = "stored-access-token";

        CookieTokenRefresher refresher = CreateRefresher(
            refreshResult: new TokenResponse
            {
                AccessToken = RefreshedAccessToken,
                RefreshToken = blankToken,
                IdToken = blankToken,
                ExpiresIn = 300
            });
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(30),
            refreshToken: "refresh-token",
            idToken: "id-token");

        await refresher.ValidateAsync(context);

        context.Principal.ShouldNotBeNull();
        context.Properties.GetTokenValue(AccessTokenName).ShouldBe(RefreshedAccessToken);
        context.Properties.GetTokenValue(RefreshTokenName).ShouldBe("refresh-token");
        context.Properties.GetTokenValue(IdTokenName).ShouldBe("id-token");
    }

    /// <summary>
    /// One second, the smallest positive lifetime, is still stored (#974).
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(300)]
    [InlineData(3600)]
    public async Task ValidateAsync_WhenRefreshAnswersWithAPositiveLifetime_StoresTheTokensAndTheNewExpiry(
        int expiresIn)
    {
        string accessToken = "stored-access-token";

        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        CookieTokenRefresher refresher = CreateRefresher(
            refreshResult: new TokenResponse
            {
                AccessToken = RefreshedAccessToken,
                RefreshToken = "rotated-refresh-token",
                IdToken = RotatedIdToken,
                ExpiresIn = expiresIn
            });
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            authenticationService,
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(30),
            refreshToken: "refresh-token",
            idToken: "id-token");

        DateTimeOffset before = DateTimeOffset.UtcNow;
        await refresher.ValidateAsync(context);
        DateTimeOffset after = DateTimeOffset.UtcNow;

        context.Principal.ShouldNotBeNull();
        context.ShouldRenew.ShouldBeTrue();
        context.Properties.GetTokenValue(AccessTokenName).ShouldBe(RefreshedAccessToken);
        context.Properties.GetTokenValue(RefreshTokenName).ShouldBe("rotated-refresh-token");
        context.Properties.GetTokenValue(IdTokenName).ShouldBe(RotatedIdToken);
        DateTimeOffset storedExpiresAt = DateTimeOffset.Parse(
            context.Properties.GetTokenValue(ExpiresAtName).ShouldNotBeNull(), CultureInfo.InvariantCulture);
        storedExpiresAt.ShouldBeInRange(before.AddSeconds(expiresIn), after.AddSeconds(expiresIn));
    }

    /// <summary>
    /// #1025: 60 seconds before expiry, or after half the lifetime when that is later.
    /// </summary>
    [Theory]
    [InlineData(1, 0.5)]
    [InlineData(2, 1.0)]
    [InlineData(59, 29.5)]
    [InlineData(60, 30.0)]
    [InlineData(61, 30.5)]
    [InlineData(119, 59.5)]
    [InlineData(120, 60.0)]
    [InlineData(121, 60.0)]
    [InlineData(300, 60.0)]
    [InlineData(3600, 60.0)]
    public async Task ValidateAsync_WhenRefreshAnswersWithALifetime_SchedulesTheNextRefreshAtMostAMinuteAndAtMostHalfTheLifetimeEarly(
        int expiresIn,
        double expectedLeadSeconds)
    {
        string accessToken = "stored-access-token";
        FakeTimeProvider time = new(Start);

        CookieTokenRefresher refresher = CreateRefresher(
            refreshResult: new TokenResponse { AccessToken = RefreshedAccessToken, ExpiresIn = expiresIn },
            timeProvider: time);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: Start.AddSeconds(30),
            refreshToken: "refresh-token");

        await refresher.ValidateAsync(context);

        DateTimeOffset.Parse(context.Properties.GetTokenValue(RefreshAtName).ShouldNotBeNull(), CultureInfo.InvariantCulture)
            .ShouldBe(Start.AddSeconds(expiresIn - expectedLeadSeconds));
    }

    /// <summary>
    /// #1025: with rotating refresh tokens, two pages loading at once could otherwise redeem the same one.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_OnTheNextPageAfterARefreshToAOneMinuteToken_DoesNotRefreshAgain()
    {
        string accessToken = "stored-access-token";
        string firstRefreshedAccessToken = "first-refreshed-access-token";

        ITokenEndpointClient tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
        tokenEndpointClient
            .RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                TokenRefreshResult.Renewed(new TokenResponse
                {
                    AccessToken = firstRefreshedAccessToken,
                    RefreshToken = "first-rotated-refresh-token",
                    ExpiresIn = 60
                }),
                TokenRefreshResult.Renewed(new TokenResponse
                {
                    AccessToken = "second-refreshed-access-token",
                    RefreshToken = "second-rotated-refresh-token",
                    ExpiresIn = 60
                }));
        CookieTokenRefresher refresher = CreateRefresher(tokenEndpointClient: tokenEndpointClient);
        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        CookieValidatePrincipalContext firstPage = CreateContext(
            accessToken,
            authenticationService,
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(30),
            refreshToken: "refresh-token");
        await refresher.ValidateAsync(firstPage);

        CookieValidatePrincipalContext nextPage = CreateContext(firstPage.Properties, authenticationService);
        await refresher.ValidateAsync(nextPage);

        nextPage.Principal.ShouldNotBeNull();
        nextPage.ShouldRenew.ShouldBeFalse();
        nextPage.Properties.GetTokenValue(AccessTokenName).ShouldBe(firstRefreshedAccessToken);
        nextPage.Properties.GetTokenValue(RefreshTokenName).ShouldBe("first-rotated-refresh-token");
    }

    /// <summary>
    /// #1025: a refresh time left in the past would make every later page refresh again.
    /// </summary>
    [Theory]
    [InlineData(60, 30)]
    [InlineData(300, 240)]
    public async Task ValidateAsync_WhenARefreshReplacesAStoredRefreshTime_MovesItForwardAndTheNextPageKeepsTheToken(
        int expiresIn,
        int secondsUntilRefresh)
    {
        string accessToken = "stored-access-token";
        string firstRefreshedAccessToken = "first-refreshed-access-token";
        FakeTimeProvider time = new(Start);

        ITokenEndpointClient tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
        tokenEndpointClient
            .RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                TokenRefreshResult.Renewed(new TokenResponse
                {
                    AccessToken = firstRefreshedAccessToken,
                    RefreshToken = "first-rotated-refresh-token",
                    ExpiresIn = expiresIn
                }),
                TokenRefreshResult.Renewed(new TokenResponse
                {
                    AccessToken = "second-refreshed-access-token",
                    RefreshToken = "second-rotated-refresh-token",
                    ExpiresIn = expiresIn
                }));
        CookieTokenRefresher refresher = CreateRefresher(
            tokenEndpointClient: tokenEndpointClient,
            timeProvider: time);
        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        CookieValidatePrincipalContext refreshingPage = CreateContext(
            accessToken,
            authenticationService,
            expiresAt: Start.AddSeconds(25),
            refreshToken: "refresh-token",
            refreshAt: Start.AddSeconds(-5));
        await refresher.ValidateAsync(refreshingPage);

        time.Advance(TimeSpan.FromSeconds(secondsUntilRefresh - 1));
        CookieValidatePrincipalContext nextPage = CreateContext(refreshingPage.Properties, authenticationService);
        await refresher.ValidateAsync(nextPage);

        DateTimeOffset.Parse(nextPage.Properties.GetTokenValue(RefreshAtName).ShouldNotBeNull(), CultureInfo.InvariantCulture)
            .ShouldBe(Start.AddSeconds(secondsUntilRefresh));
        nextPage.ShouldRenew.ShouldBeFalse();
        nextPage.Properties.GetTokenValue(RefreshTokenName).ShouldBe("first-rotated-refresh-token");
    }

    /// <summary>
    /// #1025: the stored refresh time decides, not the last 60 seconds.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_InsideTheLastMinuteBeforeTheStoredRefreshTime_KeepsTheToken()
    {
        string accessToken = "stored-access-token";

        CookieTokenRefresher refresher = CreateRefresher(
            refreshResult: new TokenResponse
            {
                AccessToken = RefreshedAccessToken,
                RefreshToken = "rotated-refresh-token",
                ExpiresIn = 60
            });
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(50),
            refreshToken: "refresh-token",
            refreshAt: DateTimeOffset.UtcNow.AddSeconds(20));

        await refresher.ValidateAsync(context);

        context.Principal.ShouldNotBeNull();
        context.ShouldRenew.ShouldBeFalse();
        context.Properties.GetTokenValue(AccessTokenName).ShouldBe(accessToken);
        context.Properties.GetTokenValue(RefreshTokenName).ShouldBe("refresh-token");
    }

    [Fact]
    public async Task ValidateAsync_OnceTheStoredRefreshTimeHasPassed_RefreshesTheToken()
    {
        string accessToken = "stored-access-token";

        CookieTokenRefresher refresher = CreateRefresher(
            refreshResult: new TokenResponse
            {
                AccessToken = RefreshedAccessToken,
                RefreshToken = "rotated-refresh-token",
                ExpiresIn = 60
            });
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(25),
            refreshToken: "refresh-token",
            refreshAt: DateTimeOffset.UtcNow.AddSeconds(-5));

        await refresher.ValidateAsync(context);

        context.Principal.ShouldNotBeNull();
        context.ShouldRenew.ShouldBeTrue();
        context.Properties.GetTokenValue(AccessTokenName).ShouldBe(RefreshedAccessToken);
        context.Properties.GetTokenValue(RefreshTokenName).ShouldBe("rotated-refresh-token");
    }

    /// <summary>
    /// #1025: an unreadable refresh time counts as none, and the refresh stores a readable one.
    /// </summary>
    [Theory]
    [InlineData("not-a-date")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ValidateAsync_InTheLastMinuteWithAnUnreadableStoredRefreshTime_RefreshesAndStoresAReadableOne(
        string storedRefreshAt)
    {
        string accessToken = "stored-access-token";
        FakeTimeProvider time = new(Start);

        CookieTokenRefresher refresher = CreateRefresher(
            refreshResult: new TokenResponse
            {
                AccessToken = RefreshedAccessToken,
                RefreshToken = "rotated-refresh-token",
                ExpiresIn = 300
            },
            timeProvider: time);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: Start.AddSeconds(30),
            refreshToken: "refresh-token",
            rawRefreshAt: storedRefreshAt);

        await refresher.ValidateAsync(context);

        context.Properties.GetTokenValue(RefreshTokenName).ShouldBe("rotated-refresh-token");
        DateTimeOffset.Parse(context.Properties.GetTokenValue(RefreshAtName).ShouldNotBeNull(), CultureInfo.InvariantCulture)
            .ShouldBe(Start.AddSeconds(240));
    }

    [Fact]
    public async Task ValidateAsync_BeforeTheLastMinuteWithAnUnreadableStoredRefreshTime_KeepsTheToken()
    {
        string accessToken = "stored-access-token";
        FakeTimeProvider time = new(Start);

        CookieTokenRefresher refresher = CreateRefresher(
            refreshResult: new TokenResponse
            {
                AccessToken = RefreshedAccessToken,
                RefreshToken = "rotated-refresh-token",
                ExpiresIn = 300
            },
            timeProvider: time);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: Start.AddSeconds(90),
            refreshToken: "refresh-token",
            rawRefreshAt: "not-a-date");

        await refresher.ValidateAsync(context);

        context.Principal.ShouldNotBeNull();
        context.ShouldRenew.ShouldBeFalse();
        context.Properties.GetTokenValue(RefreshTokenName).ShouldBe("refresh-token");
    }

    /// <summary>
    /// #1025: trusting it would let the token run out before it is refreshed.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_WhenTheStoredRefreshTimeIsLaterThanTheExpiry_RefreshesInTheLastMinute()
    {
        string accessToken = "stored-access-token";
        FakeTimeProvider time = new(Start);

        CookieTokenRefresher refresher = CreateRefresher(
            refreshResult: new TokenResponse
            {
                AccessToken = RefreshedAccessToken,
                RefreshToken = "rotated-refresh-token",
                ExpiresIn = 300
            },
            timeProvider: time);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: Start.AddSeconds(30),
            refreshToken: "refresh-token",
            refreshAt: Start.AddSeconds(45));

        await refresher.ValidateAsync(context);

        context.ShouldRenew.ShouldBeTrue();
        context.Properties.GetTokenValue(RefreshTokenName).ShouldBe("rotated-refresh-token");
    }

    /// <summary>
    /// A cookie written before #1025 keeps the old rule, so open sessions carry on.
    /// </summary>
    [Theory]
    [InlineData(55, true)]
    [InlineData(65, false)]
    public async Task ValidateAsync_WhenTheCookieHasNoStoredRefreshTime_RefreshesInTheLastMinute(
        int secondsUntilExpiry,
        bool expectRefresh)
    {
        string accessToken = "stored-access-token";

        CookieTokenRefresher refresher = CreateRefresher(
            refreshResult: new TokenResponse
            {
                AccessToken = RefreshedAccessToken,
                RefreshToken = "rotated-refresh-token",
                ExpiresIn = 300
            });
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(secondsUntilExpiry),
            refreshToken: "refresh-token");

        await refresher.ValidateAsync(context);

        context.Principal.ShouldNotBeNull();
        context.ShouldRenew.ShouldBe(expectRefresh);
        context.Properties.GetTokenValue(RefreshTokenName)
            .ShouldBe(expectRefresh ? "rotated-refresh-token" : "refresh-token");
    }

    /// <summary>
    /// #1026: with no expiry the session would never be renewed again. The cookie holds both tokens, so the
    /// expiry is the only reason.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("soon")]
    public async Task ValidateAsync_WhenTheCookieHasNoUsableExpiry_RejectsPrincipalAndSignsOut(string? expiresAt)
    {
        string accessToken = "stored-access-token";

        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        ISessionExpiryNotice sessionExpiryNotice = Substitute.For<ISessionExpiryNotice>();
        CookieTokenRefresher refresher = CreateRefresher(sessionExpiryNotice: sessionExpiryNotice);
        CookieValidatePrincipalContext context = CreateContextWithStoredTokens(
            authenticationService, accessToken, expiresAt, refreshToken: "refresh-token");

        await refresher.ValidateAsync(context);

        context.Principal.ShouldBeNull();
        await authenticationService.Received(1).SignOutAsync(
            Arg.Any<HttpContext>(),
            CookieAuthenticationDefaults.AuthenticationScheme,
            Arg.Any<AuthenticationProperties>());
        sessionExpiryNotice.Received(1).Raise();
    }

    /// <summary>
    /// #1026: the API refuses a blank token on every call, and far from expiry the refresh never runs.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public async Task ValidateAsync_WhenTheStoredAccessTokenIsBlank_RejectsPrincipalAndSignsOut(string? accessToken)
    {
        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        ISessionExpiryNotice sessionExpiryNotice = Substitute.For<ISessionExpiryNotice>();
        CookieTokenRefresher refresher = CreateRefresher(sessionExpiryNotice: sessionExpiryNotice);
        CookieValidatePrincipalContext context = CreateContextWithStoredTokens(
            authenticationService,
            accessToken,
            DateTimeOffset.UtcNow.AddHours(1).ToString("o", CultureInfo.InvariantCulture),
            refreshToken: "refresh-token");

        await refresher.ValidateAsync(context);

        context.Principal.ShouldBeNull();
        await authenticationService.Received(1).SignOutAsync(
            Arg.Any<HttpContext>(),
            CookieAuthenticationDefaults.AuthenticationScheme,
            Arg.Any<AuthenticationProperties>());
        sessionExpiryNotice.Received(1).Raise();
    }

    /// <summary>
    /// #1026: a refresh would hide the broken cookie and spend the refresh token on a dead session. Not
    /// spending it is invisible in the context, hence the check on the substitute.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_WhenTheStoredAccessTokenIsBlankNearExpiry_RejectsWithoutRedeemingTheRefreshToken()
    {
        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        ITokenEndpointClient tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
        CookieTokenRefresher refresher = CreateRefresher(
            refreshResult: new TokenResponse
            {
                AccessToken = RefreshedAccessToken,
                RefreshToken = "rotated-refresh-token",
                ExpiresIn = 300
            },
            tokenEndpointClient: tokenEndpointClient);
        CookieValidatePrincipalContext context = CreateContextWithStoredTokens(
            authenticationService,
            accessToken: "   ",
            DateTimeOffset.UtcNow.AddSeconds(30).ToString("o", CultureInfo.InvariantCulture),
            refreshToken: "refresh-token");

        await refresher.ValidateAsync(context);

        context.Principal.ShouldBeNull();
        context.ShouldRenew.ShouldBeFalse();
        context.Properties.GetTokenValue(AccessTokenName).ShouldBe("   ");
        context.Properties.GetTokenValue(RefreshTokenName).ShouldBe("refresh-token");
        await tokenEndpointClient.DidNotReceive().RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// #1026: the sign-in server could only answer with an error. Not sending it is invisible in the
    /// context, hence the check on the substitute.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public async Task ValidateAsync_WhenNearExpiryWithABlankRefreshToken_RejectsWithoutCallingTheTokenEndpoint(
        string blankRefreshToken)
    {
        string accessToken = "stored-access-token";

        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        ITokenEndpointClient tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
        CookieTokenRefresher refresher = CreateRefresher(tokenEndpointClient: tokenEndpointClient);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            authenticationService,
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(30),
            refreshToken: blankRefreshToken);

        await refresher.ValidateAsync(context);

        context.Principal.ShouldBeNull();
        await authenticationService.Received(1).SignOutAsync(
            Arg.Any<HttpContext>(),
            CookieAuthenticationDefaults.AuthenticationScheme,
            Arg.Any<AuthenticationProperties>());
        await tokenEndpointClient.DidNotReceive().RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// #1026: a blank refresh token can renew nothing. An empty one counts too: only the sign-in handler
    /// skips it.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public async Task ValidateAsync_WhenTheStoredRefreshTokenIsBlankFarFromExpiry_RejectsPrincipalAndSignsOut(
        string blankRefreshToken)
    {
        string accessToken = "stored-access-token";

        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        ISessionExpiryNotice sessionExpiryNotice = Substitute.For<ISessionExpiryNotice>();
        CookieTokenRefresher refresher = CreateRefresher(sessionExpiryNotice: sessionExpiryNotice);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            authenticationService,
            expiresAt: DateTimeOffset.UtcNow.AddHours(1),
            refreshToken: blankRefreshToken);

        await refresher.ValidateAsync(context);

        context.Principal.ShouldBeNull();
        await authenticationService.Received(1).SignOutAsync(
            Arg.Any<HttpContext>(),
            CookieAuthenticationDefaults.AuthenticationScheme,
            Arg.Any<AuthenticationProperties>());
        sessionExpiryNotice.Received(1).Raise();
    }

    /// <summary>
    /// #1026: OAuth makes the refresh token optional, so the session lives until its first renewal.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_WhenTheCookieHasNoRefreshTokenFarFromExpiry_KeepsPrincipal()
    {
        string accessToken = "stored-access-token";

        CookieTokenRefresher refresher = CreateRefresher();
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: DateTimeOffset.UtcNow.AddHours(1),
            refreshToken: null);

        await refresher.ValidateAsync(context);

        context.Principal.ShouldNotBeNull();
    }

    /// <summary>
    /// #1026: the reason logged is the call the API refused.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_WhenMarkedDeadAndTheCookieHasNoExpiry_ConsumesTheMarkerFirst()
    {
        IDeadSessionRegistry deadSessionRegistry = Substitute.For<IDeadSessionRegistry>();
        deadSessionRegistry.ConsumeAsync("the-access-token", Arg.Any<CancellationToken>()).Returns(true);
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);
        CookieTokenRefresher refresher = CreateRefresher(
            deadSessionRegistry: deadSessionRegistry,
            logger: loggerFactory.CreateLogger<CookieTokenRefresher>());
        CookieValidatePrincipalContext context = CreateContextWithStoredTokens(
            Substitute.For<IAuthenticationService>(),
            accessToken: "the-access-token",
            expiresAt: null,
            refreshToken: "refresh-token");

        await refresher.ValidateAsync(context);

        context.Principal.ShouldBeNull();
        logs.Entries.ShouldHaveSingleItem().Message.ShouldContain("marked dead by a prior 401");
    }

    /// <summary>
    /// Ending the session here first would skip the sign-out's revoke (#964) and show the "session
    /// expired" notice to a user who signed out. The notice is not visible in the context, hence the check
    /// on the substitute.
    /// </summary>
    [Theory]
    [InlineData("the-access-token", null, "refresh-token")]
    [InlineData("   ", FarFutureExpiresAt, "refresh-token")]
    [InlineData("the-access-token", FarFutureExpiresAt, "   ")]
    public async Task ValidateAsync_OnTheSignOutRequestWithAnUnusableCookie_KeepsTheSessionForTheSignOut(
        string accessToken,
        string? expiresAt,
        string refreshToken)
    {
        ISessionExpiryNotice sessionExpiryNotice = Substitute.For<ISessionExpiryNotice>();
        CookieTokenRefresher refresher = CreateRefresher(sessionExpiryNotice: sessionExpiryNotice);
        CookieValidatePrincipalContext context = CreateContextWithStoredTokens(
            Substitute.For<IAuthenticationService>(),
            accessToken,
            expiresAt,
            refreshToken,
            path: "/auth/logout",
            method: "POST");

        await refresher.ValidateAsync(context);

        context.Principal.ShouldNotBeNull();
        sessionExpiryNotice.DidNotReceive().Raise();
    }

    /// <summary>
    /// #1026: such a cookie means the sign-in path was broken, so it is a warning.
    /// </summary>
    [Theory]
    [InlineData("the-access-token", null, "missing or unreadable expires_at")]
    [InlineData("the-access-token", "soon", "missing or unreadable expires_at")]
    [InlineData(null, FarFutureExpiresAt, "missing, empty or blank access_token")]
    [InlineData("   ", FarFutureExpiresAt, "missing, empty or blank access_token")]
    [InlineData("the-access-token", FarFutureExpiresAt, "empty or blank refresh_token", "   ")]
    public async Task ValidateAsync_WhenTheCookieIsUnusable_LogsOneWarningWithTheReason(
        string? accessToken,
        string? expiresAt,
        string expectedReason,
        string refreshToken = "refresh-token")
    {
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);
        CookieTokenRefresher refresher = CreateRefresher(logger: loggerFactory.CreateLogger<CookieTokenRefresher>());
        CookieValidatePrincipalContext context = CreateContextWithStoredTokens(
            Substitute.For<IAuthenticationService>(),
            accessToken,
            expiresAt,
            refreshToken);

        await refresher.ValidateAsync(context);

        CapturingLoggerProvider.LogEntry entry = logs.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldContain(expectedReason);
    }

    [Fact]
    public async Task ValidateAsync_WhenSessionMarkedDead_RejectsPrincipalAndSignsOut()
    {
        string accessToken = "stored-access-token";

        IDeadSessionRegistry deadSessionRegistry = Substitute.For<IDeadSessionRegistry>();
        deadSessionRegistry
            .ConsumeAsync(accessToken, Arg.Any<CancellationToken>())
            .Returns(true);

        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        CookieTokenRefresher refresher = CreateRefresher(deadSessionRegistry: deadSessionRegistry);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken, authenticationService, expiresAt: DateTimeOffset.UtcNow.AddHours(1));

        await refresher.ValidateAsync(context);

        context.Principal.ShouldBeNull();
        await authenticationService.Received(1).SignOutAsync(
            Arg.Any<HttpContext>(),
            CookieAuthenticationDefaults.AuthenticationScheme,
            Arg.Any<AuthenticationProperties>());
    }

    [Fact]
    public async Task ValidateAsync_WhenSessionAlive_DoesNotRaiseExpiryNotice()
    {
        string accessToken = "stored-access-token";

        ISessionExpiryNotice sessionExpiryNotice = Substitute.For<ISessionExpiryNotice>();
        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        CookieTokenRefresher refresher = CreateRefresher(sessionExpiryNotice: sessionExpiryNotice);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken, authenticationService, expiresAt: DateTimeOffset.UtcNow.AddHours(1));

        await refresher.ValidateAsync(context);

        sessionExpiryNotice.DidNotReceive().Raise();
    }

    [Fact]
    public async Task ValidateAsync_WhenSessionMarkedDead_RaisesOneShotExpiryNotice()
    {
        string accessToken = "stored-access-token";

        IDeadSessionRegistry deadSessionRegistry = Substitute.For<IDeadSessionRegistry>();
        deadSessionRegistry
            .ConsumeAsync(accessToken, Arg.Any<CancellationToken>())
            .Returns(true);

        ISessionExpiryNotice sessionExpiryNotice = Substitute.For<ISessionExpiryNotice>();
        CookieTokenRefresher refresher = CreateRefresher(
            deadSessionRegistry: deadSessionRegistry,
            sessionExpiryNotice: sessionExpiryNotice);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken, Substitute.For<IAuthenticationService>(), expiresAt: DateTimeOffset.UtcNow.AddHours(1));

        await refresher.ValidateAsync(context);

        context.Principal.ShouldBeNull();
        sessionExpiryNotice.Received(1).Raise();
    }

    /// <summary>
    /// A refresh first would redeem the token the sign-out revokes, and OpenIddict accepts a redeemed token
    /// for its reuse window, so a copied cookie's token would survive (#964, #1027).
    /// </summary>
    [Theory]
    [InlineData("/auth/logout")]
    [InlineData("/AUTH/Logout")]
    [InlineData("/auth/logout/")]
    [InlineData("/auth/local-signout")]
    [InlineData("/auth/local-signout/")]
    public async Task ValidateAsync_OnTheSignOutRequestNearExpiry_KeepsTheStoredRefreshTokenUnredeemed(string path)
    {
        string accessToken = "stored-access-token";

        CookieTokenRefresher refresher = CreateRefresher(
            refreshResult: new TokenResponse
            {
                AccessToken = RefreshedAccessToken,
                RefreshToken = "rotated-refresh-token",
                ExpiresIn = 300
            });
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(30),
            refreshToken: "refresh-token",
            path: path,
            method: "POST");

        await refresher.ValidateAsync(context);

        context.Principal.ShouldNotBeNull();
        context.ShouldRenew.ShouldBeFalse();
        context.Properties.GetTokenValue(RefreshTokenName).ShouldBe("refresh-token");
    }

    /// <summary>
    /// Only what routing sends to a sign-out handler skips the check. Skipping a GET to the same path (the
    /// 405 page) would let a dead session through one more request.
    /// </summary>
    [Theory]
    [InlineData("POST", "/auth/logout-everywhere")]
    [InlineData("POST", "/auth/logoutx")]
    [InlineData("POST", "/auth/logout/extra")]
    [InlineData("POST", "/auth/login")]
    [InlineData("POST", "/auth/local-signoutx")]
    [InlineData("GET", "/auth/local-signout")]
    [InlineData("GET", "/auth/logout")]
    [InlineData("HEAD", "/auth/logout")]
    [InlineData("PUT", "/auth/logout")]
    public async Task ValidateAsync_OnARequestThatOnlyLooksLikeTheSignOut_StillRefreshes(string method, string path)
    {
        string accessToken = "stored-access-token";

        CookieTokenRefresher refresher = CreateRefresher(
            refreshResult: new TokenResponse
            {
                AccessToken = RefreshedAccessToken,
                RefreshToken = "rotated-refresh-token",
                ExpiresIn = 300
            });
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(30),
            refreshToken: "refresh-token",
            path: path,
            method: method);

        await refresher.ValidateAsync(context);

        context.Properties.GetTokenValue(RefreshTokenName).ShouldBe("rotated-refresh-token");
    }

    /// <summary>
    /// Rejecting first would hide the tokens from the sign-out and show the "session expired" notice to a
    /// user who signed out.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_OnTheSignOutRequestWhenMarkedDead_KeepsTheSessionForTheSignOut()
    {
        string accessToken = "stored-access-token";

        IDeadSessionRegistry deadSessionRegistry = Substitute.For<IDeadSessionRegistry>();
        deadSessionRegistry.ConsumeAsync(accessToken, Arg.Any<CancellationToken>()).Returns(true);
        ISessionExpiryNotice sessionExpiryNotice = Substitute.For<ISessionExpiryNotice>();
        CookieTokenRefresher refresher = CreateRefresher(
            deadSessionRegistry: deadSessionRegistry,
            sessionExpiryNotice: sessionExpiryNotice);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: DateTimeOffset.UtcNow.AddHours(1),
            refreshToken: "refresh-token",
            path: "/auth/logout",
            method: "POST");

        await refresher.ValidateAsync(context);

        context.Principal.ShouldNotBeNull();
        sessionExpiryNotice.DidNotReceive().Raise();
    }

    /// <summary>
    /// #1061: the marker names the token, not the user.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_WhenAnotherDeviceOfTheSameUserWasMarkedDead_KeepsThisSession()
    {
        string laptopAccessToken = "laptop-access-token";
        string phoneAccessToken = "phone-access-token";
        DeadSessionRegistry deadSessionRegistry = DeadSessionRegistryFactory.Create();
        await deadSessionRegistry.MarkDeadAsync(laptopAccessToken);

        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        CookieTokenRefresher refresher = CreateRefresher(deadSessionRegistry: deadSessionRegistry);
        CookieValidatePrincipalContext phone = CreateContext(
            phoneAccessToken, authenticationService, expiresAt: DateTimeOffset.UtcNow.AddHours(1));

        await refresher.ValidateAsync(phone);

        phone.Principal.ShouldNotBeNull();
        await authenticationService.DidNotReceive().SignOutAsync(
            Arg.Any<HttpContext>(),
            Arg.Any<string>(),
            Arg.Any<AuthenticationProperties>());
    }

    /// <summary>
    /// #1061: another device's page must not consume this device's marker.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_WhenAnotherDeviceLoadsAPageFirst_StillSignsOutTheDeviceWhoseCallWasRefused()
    {
        string laptopAccessToken = "laptop-access-token";
        string phoneAccessToken = "phone-access-token";
        DeadSessionRegistry deadSessionRegistry = DeadSessionRegistryFactory.Create();
        await deadSessionRegistry.MarkDeadAsync(laptopAccessToken);

        CookieTokenRefresher refresher = CreateRefresher(deadSessionRegistry: deadSessionRegistry);
        CookieValidatePrincipalContext phone = CreateContext(
            phoneAccessToken, Substitute.For<IAuthenticationService>(), expiresAt: DateTimeOffset.UtcNow.AddHours(1));
        CookieValidatePrincipalContext laptop = CreateContext(
            laptopAccessToken, Substitute.For<IAuthenticationService>(), expiresAt: DateTimeOffset.UtcNow.AddHours(1));

        await refresher.ValidateAsync(phone);
        await refresher.ValidateAsync(laptop);

        laptop.Principal.ShouldBeNull();
    }

    /// <summary>
    /// #1061: a sign-out on another device must not clear this device's marker.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_OnTheSignOutRequestOfAnotherDevice_StillSignsOutTheDeviceWhoseCallWasRefused()
    {
        string laptopAccessToken = "laptop-access-token";
        string phoneAccessToken = "phone-access-token";
        DeadSessionRegistry deadSessionRegistry = DeadSessionRegistryFactory.Create();
        await deadSessionRegistry.MarkDeadAsync(laptopAccessToken);

        CookieTokenRefresher refresher = CreateRefresher(deadSessionRegistry: deadSessionRegistry);
        CookieValidatePrincipalContext phoneSignOut = CreateContext(
            phoneAccessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: DateTimeOffset.UtcNow.AddHours(1),
            path: "/auth/logout",
            method: "POST");
        CookieValidatePrincipalContext laptop = CreateContext(
            laptopAccessToken, Substitute.For<IAuthenticationService>(), expiresAt: DateTimeOffset.UtcNow.AddHours(1));

        await refresher.ValidateAsync(phoneSignOut);
        await refresher.ValidateAsync(laptop);

        laptop.Principal.ShouldBeNull();
    }

    /// <summary>
    /// A closing tab's sign-out arrives already cancelled, and the dead-session lookup would throw. Nothing
    /// may run first, or the sign-out never reads the refresh token it has to revoke (#964).
    /// </summary>
    [Fact]
    public async Task ValidateAsync_OnTheSignOutRequestAfterTheBrowserLeft_KeepsTheSessionWithoutChecks()
    {
        string accessToken = "stored-access-token";

        IDeadSessionRegistry deadSessionRegistry = Substitute.For<IDeadSessionRegistry>();
        deadSessionRegistry
            .ConsumeAsync(Arg.Any<string>(), Arg.Is<CancellationToken>(token => token.IsCancellationRequested))
            .Returns(_ => ValueTask.FromException<bool>(new OperationCanceledException()));

        CookieTokenRefresher refresher = CreateRefresher(deadSessionRegistry: deadSessionRegistry);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(30),
            refreshToken: "refresh-token",
            path: "/auth/logout",
            requestAborted: new CancellationToken(canceled: true),
            method: "POST");

        await refresher.ValidateAsync(context);

        context.Principal.ShouldNotBeNull();
        context.Properties.GetTokenValue(RefreshTokenName).ShouldBe("refresh-token");
    }

    /// <summary>
    /// #1027: the 401 may come from the TMS API, which revokes nothing, so the refresh token can still be
    /// valid. The session ends for a real reason, so a browser that left does not stop the revoke.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidateAsync_WhenSessionMarkedDead_RevokesTheStoredRefreshToken(bool browserLeft)
    {
        string accessToken = "stored-access-token";

        IDeadSessionRegistry deadSessionRegistry = Substitute.For<IDeadSessionRegistry>();
        deadSessionRegistry.ConsumeAsync(accessToken, Arg.Any<CancellationToken>()).Returns(true);
        ITokenEndpointClient tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
        CookieTokenRefresher refresher = CreateRefresher(
            deadSessionRegistry: deadSessionRegistry,
            tokenEndpointClient: tokenEndpointClient);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: DateTimeOffset.UtcNow.AddHours(1),
            refreshToken: "refresh-token",
            requestAborted: new CancellationToken(canceled: browserLeft));

        await refresher.ValidateAsync(context);

        RevokedRefreshTokens(tokenEndpointClient).ShouldBe(["refresh-token"]);
    }

    /// <summary>
    /// #1027: the auth server already swapped the stored token for the new one. The old one is accepted for
    /// OpenIddict's reuse window and the new one for hours, so both are revoked.
    /// </summary>
    [Theory]
    [InlineData(null, 300)]
    [InlineData("   ", 300)]
    [InlineData(RefreshedAccessToken, null)]
    [InlineData(RefreshedAccessToken, 0)]
    public async Task ValidateAsync_WhenRefreshAnswerIsUnusable_RevokesTheStoredAndTheReceivedRefreshToken(
        string? refreshedAccessToken,
        int? expiresIn)
    {
        string accessToken = "stored-access-token";

        ITokenEndpointClient tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
        CookieTokenRefresher refresher = CreateRefresher(
            refreshResult: new TokenResponse
            {
                AccessToken = refreshedAccessToken,
                RefreshToken = "rotated-refresh-token",
                ExpiresIn = expiresIn
            },
            tokenEndpointClient: tokenEndpointClient);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(30),
            refreshToken: "refresh-token");

        await refresher.ValidateAsync(context);

        RevokedRefreshTokens(tokenEndpointClient).ShouldBe(["refresh-token", "rotated-refresh-token"], ignoreOrder: true);
    }

    /// <summary>
    /// Only the stored token is left to revoke, and it is revoked once.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("refresh-token")]
    public async Task ValidateAsync_WhenUnusableRefreshAnswerCarriesNoNewRefreshToken_RevokesTheStoredOneOnce(
        string? receivedRefreshToken)
    {
        string accessToken = "stored-access-token";

        ITokenEndpointClient tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
        CookieTokenRefresher refresher = CreateRefresher(
            refreshResult: new TokenResponse { AccessToken = null, RefreshToken = receivedRefreshToken, ExpiresIn = 300 },
            tokenEndpointClient: tokenEndpointClient);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(30),
            refreshToken: "refresh-token");

        await refresher.ValidateAsync(context);

        RevokedRefreshTokens(tokenEndpointClient).ShouldBe(["refresh-token"]);
    }

    /// <summary>
    /// #1027: a refused refresh is not always a dead token. A lockout revokes nothing, and the token works
    /// again once it ends.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_WhenNearExpiryAndTheRefreshIsRefused_RevokesTheStoredRefreshToken()
    {
        string accessToken = "stored-access-token";

        ITokenEndpointClient tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
        CookieTokenRefresher refresher = CreateRefresher(
            tokenEndpointClient: tokenEndpointClient,
            refreshFailure: TokenRefreshResult.FinalFailure);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(30),
            refreshToken: "refresh-token");

        await refresher.ValidateAsync(context);

        RevokedRefreshTokens(tokenEndpointClient).ShouldBe(["refresh-token"]);
    }

    /// <summary>
    /// The browser never gets the deleted cookie and sends the same refresh token again, so a revoke here
    /// would sign out a user who only clicked a second link.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_WhenTheBrowserLeavesDuringTheRefreshAfterTheAccessTokenRanOut_KeepsTheStoredRefreshTokenAlive()
    {
        string accessToken = "stored-access-token";

        ITokenEndpointClient tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
        CookieTokenRefresher refresher = CreateRefresher(
            tokenEndpointClient: tokenEndpointClient,
            refreshFailure: TokenRefreshResult.TemporaryFailure);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1),
            refreshToken: "refresh-token",
            requestAborted: new CancellationToken(canceled: true));

        await refresher.ValidateAsync(context);

        RevokedRefreshTokens(tokenEndpointClient).ShouldBeEmpty();
    }

    [Fact]
    public async Task ValidateAsync_WhenNearExpiryWithoutRefreshToken_RevokesNothing()
    {
        string accessToken = "stored-access-token";

        ITokenEndpointClient tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
        CookieTokenRefresher refresher = CreateRefresher(tokenEndpointClient: tokenEndpointClient);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(30),
            refreshToken: null);

        await refresher.ValidateAsync(context);

        RevokedRefreshTokens(tokenEndpointClient).ShouldBeEmpty();
    }

    /// <summary>
    /// A normal page load pays no extra call to the auth server (#940).
    /// </summary>
    [Fact]
    public async Task ValidateAsync_WhenSessionAlive_RevokesNothing()
    {
        string accessToken = "stored-access-token";

        ITokenEndpointClient tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
        CookieTokenRefresher refresher = CreateRefresher(tokenEndpointClient: tokenEndpointClient);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: DateTimeOffset.UtcNow.AddHours(1),
            refreshToken: "refresh-token");

        await refresher.ValidateAsync(context);

        RevokedRefreshTokens(tokenEndpointClient).ShouldBeEmpty();
    }

    [Fact]
    public async Task ValidateAsync_WhenRefreshSucceeds_RevokesNothing()
    {
        string accessToken = "stored-access-token";

        ITokenEndpointClient tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
        CookieTokenRefresher refresher = CreateRefresher(
            refreshResult: new TokenResponse
            {
                AccessToken = RefreshedAccessToken,
                RefreshToken = "rotated-refresh-token",
                ExpiresIn = 300
            },
            tokenEndpointClient: tokenEndpointClient);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(30),
            refreshToken: "refresh-token");

        await refresher.ValidateAsync(context);

        RevokedRefreshTokens(tokenEndpointClient).ShouldBeEmpty();
    }

    /// <summary>
    /// #1027: even with two tokens to revoke.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_WhenTheAuthServerNeverAnswersTheRevokes_HoldsTheRequestNoLongerThanTheSignOutTimeLimit()
    {
        string accessToken = "stored-access-token";

        TaskCompletionSource bothRevokesStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int startedRevokes = 0;
        ITokenEndpointClient tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
        tokenEndpointClient
            .RevokeRefreshTokenAsync(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (Interlocked.Increment(ref startedRevokes) == 2)
                {
                    bothRevokesStarted.TrySetResult();
                }

                return WaitUntilCancelledAsync(call.ArgAt<CancellationToken>(2));
            });
        FakeTimeProvider time = new();
        CookieTokenRefresher refresher = CreateRefresher(
            refreshResult: new TokenResponse { AccessToken = null, RefreshToken = "rotated-refresh-token", ExpiresIn = 300 },
            tokenEndpointClient: tokenEndpointClient,
            timeProvider: time);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: time.GetUtcNow().AddSeconds(30),
            refreshToken: "refresh-token");

        Task validation = refresher.ValidateAsync(context);
        await bothRevokesStarted.Task.WaitAsync(TestTimeout);
        time.Advance(RefreshTokenRevoker.TimeLimit - TimeSpan.FromTicks(1));
        bool finishedBeforeTheLimit = validation.IsCompleted;
        time.Advance(TimeSpan.FromTicks(1));
        await validation.WaitAsync(TestTimeout);

        finishedBeforeTheLimit.ShouldBeFalse();
    }

    /// <summary>
    /// An exception reaching the cookie handler would turn into an error page.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_WhenTheRevokeThrows_StillEndsTheSession()
    {
        string accessToken = "stored-access-token";

        IDeadSessionRegistry deadSessionRegistry = Substitute.For<IDeadSessionRegistry>();
        deadSessionRegistry.ConsumeAsync(accessToken, Arg.Any<CancellationToken>()).Returns(true);
        ITokenEndpointClient tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
        tokenEndpointClient
            .RevokeRefreshTokenAsync(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("A new handler failed.")));
        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        CookieTokenRefresher refresher = CreateRefresher(
            deadSessionRegistry: deadSessionRegistry,
            tokenEndpointClient: tokenEndpointClient);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            authenticationService,
            expiresAt: DateTimeOffset.UtcNow.AddHours(1),
            refreshToken: "refresh-token");

        await refresher.ValidateAsync(context);

        context.Principal.ShouldBeNull();
        await authenticationService.Received(1).SignOutAsync(
            Arg.Any<HttpContext>(),
            CookieAuthenticationDefaults.AuthenticationScheme,
            Arg.Any<AuthenticationProperties>());
    }

    private static CookieTokenRefresher CreateRefresher(
        TokenResponse? refreshResult = null,
        IDeadSessionRegistry? deadSessionRegistry = null,
        ISessionExpiryNotice? sessionExpiryNotice = null,
        ILogger<CookieTokenRefresher>? logger = null,
        ITokenEndpointClient? tokenEndpointClient = null,
        TimeProvider? timeProvider = null,
        TokenRefreshResult? refreshFailure = null)
    {
        // A token client the test passes in with no answer keeps the answer the test set up on it.
        bool setsUpTheRefresh = tokenEndpointClient is null || refreshResult is not null || refreshFailure is not null;
        tokenEndpointClient ??= Substitute.For<ITokenEndpointClient>();
        if (setsUpTheRefresh)
        {
            tokenEndpointClient
                .RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(refreshResult is not null
                    ? TokenRefreshResult.Renewed(refreshResult)
                    : refreshFailure ?? TokenRefreshResult.FinalFailure);
        }

        // The revoke reads the revocation endpoint from the discovery document.
        OpenIdConnectOptions openIdConnectOptions = new()
        {
            ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                new OpenIdConnectConfiguration { RevocationEndpoint = RevocationEndpoint })
        };

        IOptionsMonitor<OpenIdConnectOptions> optionsMonitor =
            Substitute.For<IOptionsMonitor<OpenIdConnectOptions>>();
        optionsMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme).Returns(openIdConnectOptions);

        RefreshTokenRevoker refreshTokenRevoker = new(
            tokenEndpointClient,
            optionsMonitor,
            timeProvider ?? TimeProvider.System,
            NullLogger<RefreshTokenRevoker>.Instance);

        return new CookieTokenRefresher(
            tokenEndpointClient,
            deadSessionRegistry ?? Substitute.For<IDeadSessionRegistry>(),
            sessionExpiryNotice ?? Substitute.For<ISessionExpiryNotice>(),
            refreshTokenRevoker,
            timeProvider ?? TimeProvider.System,
            logger ?? NullLogger<CookieTokenRefresher>.Instance);
    }

    /// <summary>
    /// A revoke is not visible in the result, so the calls to the token client, the boundary to the auth
    /// server, are read instead.
    /// </summary>
    private static string[] RevokedRefreshTokens(ITokenEndpointClient tokenEndpointClient) =>
        tokenEndpointClient.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ITokenEndpointClient.RevokeRefreshTokenAsync))
            .Where(call => Equals(call.GetArguments()[0], new Uri(RevocationEndpoint)))
            .Select(call => call.GetArguments()[1])
            .OfType<string>()
            .ToArray();

    /// <summary>
    /// Like the real token client, a revoke cut off by its time limit returns quietly instead of throwing.
    /// </summary>
    private static async Task WaitUntilCancelledAsync(CancellationToken cancellationToken) =>
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    private static CookieValidatePrincipalContext CreateContext(
        string accessToken,
        IAuthenticationService authenticationService,
        DateTimeOffset expiresAt,
        string? refreshToken = null,
        string? idToken = null,
        string path = "/",
        CancellationToken requestAborted = default,
        string method = "GET",
        DateTimeOffset? refreshAt = null,
        string? rawRefreshAt = null)
    {
        return CreateContextWithStoredTokens(
            authenticationService,
            accessToken,
            expiresAt.ToString("o", CultureInfo.InvariantCulture),
            refreshToken,
            idToken,
            path,
            requestAborted,
            method,
            rawRefreshAt ?? refreshAt?.ToString("o", CultureInfo.InvariantCulture));
    }

    /// <summary>A null token is left out of the cookie; any other value is stored as it is.</summary>
    private static CookieValidatePrincipalContext CreateContextWithStoredTokens(
        IAuthenticationService authenticationService,
        string? accessToken,
        string? expiresAt,
        string? refreshToken = null,
        string? idToken = null,
        string path = "/",
        CancellationToken requestAborted = default,
        string method = "GET",
        string? refreshAt = null)
    {
        List<AuthenticationToken> tokens = [];

        if (accessToken is not null)
        {
            tokens.Add(new AuthenticationToken { Name = AccessTokenName, Value = accessToken });
        }

        if (expiresAt is not null)
        {
            tokens.Add(new AuthenticationToken { Name = ExpiresAtName, Value = expiresAt });
        }

        if (refreshToken is not null)
        {
            tokens.Add(new AuthenticationToken { Name = RefreshTokenName, Value = refreshToken });
        }

        if (idToken is not null)
        {
            tokens.Add(new AuthenticationToken { Name = IdTokenName, Value = idToken });
        }

        if (refreshAt is not null)
        {
            tokens.Add(new AuthenticationToken { Name = RefreshAtName, Value = refreshAt });
        }

        AuthenticationProperties properties = new();
        properties.StoreTokens(tokens);

        return CreateContext(properties, authenticationService, path, requestAborted, method);
    }

    /// <summary>
    /// The next request of the same browser, whose cookie carries the tokens an earlier request stored.
    /// </summary>
    private static CookieValidatePrincipalContext CreateContext(
        AuthenticationProperties properties,
        IAuthenticationService authenticationService,
        string path = "/",
        CancellationToken requestAborted = default,
        string method = "GET")
    {
        ServiceCollection services = new();
        services.AddSingleton(authenticationService);

        DefaultHttpContext httpContext = new()
        {
            RequestServices = services.BuildServiceProvider(),
            RequestAborted = requestAborted
        };
        httpContext.Request.Path = path;
        httpContext.Request.Method = method;

        ClaimsPrincipal principal = new(new ClaimsIdentity(
            [new Claim("sub", Subject)],
            CookieAuthenticationDefaults.AuthenticationScheme));

        AuthenticationTicket ticket = new(
            principal, properties, CookieAuthenticationDefaults.AuthenticationScheme);

        AuthenticationScheme scheme = new(
            CookieAuthenticationDefaults.AuthenticationScheme,
            displayName: null,
            handlerType: typeof(CookieAuthenticationHandler));

        return new CookieValidatePrincipalContext(
            httpContext, scheme, new CookieAuthenticationOptions(), ticket);
    }
}
