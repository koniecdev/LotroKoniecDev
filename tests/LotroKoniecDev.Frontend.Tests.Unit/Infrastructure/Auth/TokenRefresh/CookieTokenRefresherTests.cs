using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;
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
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Auth.TokenRefresh;

public sealed class CookieTokenRefresherTests : IDisposable
{
    private const string DiscoveryIssuer = "https://localhost:5003";
    private const string Subject = "11111111-1111-1111-1111-111111111111";
    private const string AccessTokenName = "access_token";
    private const string RefreshTokenName = "refresh_token";
    private const string IdTokenName = "id_token";
    private const string ExpiresAtName = "expires_at";
    private const string RefreshAtName = "refresh_at";

    private static readonly DateTimeOffset Start = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly List<RSA> _rsaInstances = [];

    [Fact]
    public async Task ValidateAsync_WithUnexpiredTokenSignedByRotatedKey_RejectsPrincipalAndSignsOut()
    {
        // The token is still valid by the local clock, but its signature does not match the only key the
        // frontend trusts right now, because the key upstream has changed.
        RsaSecurityKey actualSigningKey = CreateRsaKey();
        RsaSecurityKey trustedKey = CreateRsaKey();
        string accessToken = MintAccessToken(actualSigningKey, tokenIssuer: DiscoveryIssuer);

        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        CookieTokenRefresher refresher = CreateRefresher(trustedKeys: [trustedKey], discoveryIssuer: DiscoveryIssuer);
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
    public async Task ValidateAsync_WithUnexpiredTokenSignedByTrustedKey_KeepsPrincipal()
    {
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);

        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        CookieTokenRefresher refresher = CreateRefresher(trustedKeys: [signingKey], discoveryIssuer: DiscoveryIssuer);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken, authenticationService, expiresAt: DateTimeOffset.UtcNow.AddHours(1));

        await refresher.ValidateAsync(context);

        context.Principal.ShouldNotBeNull();
        await authenticationService.DidNotReceive().SignOutAsync(
            Arg.Any<HttpContext>(),
            Arg.Any<string>(),
            Arg.Any<AuthenticationProperties>());
    }

    [Fact]
    public async Task ValidateAsync_WithTrustedKeyButTokenIssuerMismatch_RejectsPrincipal()
    {
        // The signature verifies against the trusted key, but the token 'iss' differs from the
        // discovery issuer. Anchoring on configuration.Issuer means this must be rejected.
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: "https://evil.example.com");

        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        CookieTokenRefresher refresher = CreateRefresher(trustedKeys: [signingKey], discoveryIssuer: DiscoveryIssuer);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken, authenticationService, expiresAt: DateTimeOffset.UtcNow.AddHours(1));

        await refresher.ValidateAsync(context);

        context.Principal.ShouldBeNull();
    }

    [Fact]
    public async Task ValidateAsync_WhenDiscoveryIssuerIsEmpty_KeepsPrincipal()
    {
        // Discovery has not given us an issuer yet. With no issuer we skip the signature check instead of
        // logging the user out by mistake, even when the key is one the frontend does not trust.
        RsaSecurityKey actualSigningKey = CreateRsaKey();
        RsaSecurityKey trustedKey = CreateRsaKey();
        string accessToken = MintAccessToken(actualSigningKey, tokenIssuer: DiscoveryIssuer);

        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        CookieTokenRefresher refresher = CreateRefresher(trustedKeys: [trustedKey], discoveryIssuer: null);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken, authenticationService, expiresAt: DateTimeOffset.UtcNow.AddHours(1));

        await refresher.ValidateAsync(context);

        context.Principal.ShouldNotBeNull();
    }

    [Fact]
    public async Task ValidateAsync_WhenTokenRefreshedNearExpiry_SkipsProactiveProbeAndStoresFreshToken()
    {
        // A token within 60 seconds of expiry is refreshed. The new token is signed with a key the
        // frontend does not trust yet, which is the short window where our copy of the keys is out of
        // date. Because the token was just issued upstream, the signature check is skipped, the session
        // survives and the token is stored.
        RsaSecurityKey trustedKey = CreateRsaKey();
        RsaSecurityKey freshUpstreamKey = CreateRsaKey();
        string staleAccessToken = MintAccessToken(trustedKey, tokenIssuer: DiscoveryIssuer);
        string refreshedAccessToken = MintAccessToken(freshUpstreamKey, tokenIssuer: DiscoveryIssuer);

        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [trustedKey],
            discoveryIssuer: DiscoveryIssuer,
            refreshResult: new TokenResponse { AccessToken = refreshedAccessToken, ExpiresIn = 3600 });
        CookieValidatePrincipalContext context = CreateContext(
            staleAccessToken,
            authenticationService,
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(30),
            refreshToken: "refresh-token");

        await refresher.ValidateAsync(context);

        context.Principal.ShouldNotBeNull();
        context.ShouldRenew.ShouldBeTrue();
        context.Properties.GetTokenValue(AccessTokenName).ShouldBe(refreshedAccessToken);
    }

    [Fact]
    public async Task ValidateAsync_WhenNearExpiryWithoutRefreshToken_RejectsPrincipal()
    {
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);

        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        CookieTokenRefresher refresher = CreateRefresher(trustedKeys: [signingKey], discoveryIssuer: DiscoveryIssuer);
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
    public async Task ValidateAsync_WhenNearExpiryAndRefreshFails_RejectsPrincipalAndSignsOut()
    {
        // No refresh result is set up, so the token client answers null. That is what it returns for any
        // failed refresh grant, a redirect included (#899).
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);

        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        CookieTokenRefresher refresher = CreateRefresher(trustedKeys: [signingKey], discoveryIssuer: DiscoveryIssuer);
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
    /// #974: the API refuses a blank token on every call, so storing it would cost the user a page before
    /// the session is ended. The answer also carries new tokens, and none of them may be stored.
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
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.AddSeconds(30);

        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [signingKey],
            discoveryIssuer: DiscoveryIssuer,
            refreshResult: new TokenResponse
            {
                AccessToken = blankAccessToken,
                RefreshToken = "rotated-refresh-token",
                IdToken = "rotated-id-token",
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
    /// #974, owner decision: a token with no lifetime would look expired at once, so every page would
    /// redeem the refresh token again. Such an answer ends the session like any other bad answer.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task ValidateAsync_WhenRefreshAnswersWithoutAPositiveLifetime_RejectsPrincipalAndStoresNothing(
        int? expiresIn)
    {
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.AddSeconds(30);

        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [signingKey],
            discoveryIssuer: DiscoveryIssuer,
            refreshResult: new TokenResponse
            {
                AccessToken = "refreshed-access-token",
                RefreshToken = "rotated-refresh-token",
                IdToken = "rotated-id-token",
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
    /// The session ends either way, so the log is the only place that says why. A warning, because our own
    /// sign-in server never sends such an answer, so something between us and it is broken.
    /// </summary>
    [Theory]
    [InlineData(null, 300, "missing, empty or blank access_token")]
    [InlineData("", 300, "missing, empty or blank access_token")]
    [InlineData("   ", 300, "missing, empty or blank access_token")]
    [InlineData("refreshed-access-token", null, "expires_in: missing")]
    [InlineData("refreshed-access-token", 0, "expires_in: 0")]
    [InlineData("refreshed-access-token", -5, "expires_in: -5")]
    public async Task ValidateAsync_WhenRefreshAnswerIsUnusable_LogsOneWarningWithTheReason(
        string? refreshedAccessToken,
        int? expiresIn,
        string expectedReason)
    {
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);

        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [signingKey],
            discoveryIssuer: DiscoveryIssuer,
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
    /// Every rejection raises the one-time "session expired" notice, and an unusable answer is no
    /// exception. The notice is not visible in the context, hence the check on the substitute.
    /// </summary>
    [Theory]
    [InlineData(null, 300)]
    [InlineData("   ", 300)]
    [InlineData("refreshed-access-token", null)]
    [InlineData("refreshed-access-token", 0)]
    public async Task ValidateAsync_WhenRefreshAnswerIsUnusable_RaisesTheExpiryNotice(
        string? refreshedAccessToken,
        int? expiresIn)
    {
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);

        ISessionExpiryNotice sessionExpiryNotice = Substitute.For<ISessionExpiryNotice>();
        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [signingKey],
            discoveryIssuer: DiscoveryIssuer,
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
    /// #974: a blank refresh or ID token counts as no token, like an empty one. It must not replace the
    /// stored one: a blank refresh token would fail the next refresh, and a blank ID token would leave the
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
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);

        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [signingKey],
            discoveryIssuer: DiscoveryIssuer,
            refreshResult: new TokenResponse
            {
                AccessToken = "refreshed-access-token",
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
        context.Properties.GetTokenValue(AccessTokenName).ShouldBe("refreshed-access-token");
        context.Properties.GetTokenValue(RefreshTokenName).ShouldBe("refresh-token");
        context.Properties.GetTokenValue(IdTokenName).ShouldBe("id-token");
    }

    /// <summary>
    /// #974 changes nothing for an answer with a positive lifetime. It rejects only a lifetime of zero or
    /// less, so one second, the smallest positive value, is still stored.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(300)]
    [InlineData(3600)]
    public async Task ValidateAsync_WhenRefreshAnswersWithAPositiveLifetime_StoresTheTokensAndTheNewExpiry(
        int expiresIn)
    {
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);

        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [signingKey],
            discoveryIssuer: DiscoveryIssuer,
            refreshResult: new TokenResponse
            {
                AccessToken = "refreshed-access-token",
                RefreshToken = "rotated-refresh-token",
                IdToken = "rotated-id-token",
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
        context.Properties.GetTokenValue(AccessTokenName).ShouldBe("refreshed-access-token");
        context.Properties.GetTokenValue(RefreshTokenName).ShouldBe("rotated-refresh-token");
        context.Properties.GetTokenValue(IdTokenName).ShouldBe("rotated-id-token");
        DateTimeOffset storedExpiresAt = DateTimeOffset.Parse(
            context.Properties.GetTokenValue(ExpiresAtName).ShouldNotBeNull(), CultureInfo.InvariantCulture);
        storedExpiresAt.ShouldBeInRange(before.AddSeconds(expiresIn), after.AddSeconds(expiresIn));
    }

    /// <summary>
    /// #1025: a token is refreshed 60 seconds before it runs out, or after half of its lifetime when that
    /// comes later. With a fixed 60 seconds, a token of a minute or less would be due again the moment it
    /// arrived.
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
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);
        FakeTimeProvider time = new(Start);

        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [signingKey],
            discoveryIssuer: DiscoveryIssuer,
            refreshResult: new TokenResponse { AccessToken = "refreshed-access-token", ExpiresIn = expiresIn },
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
    /// #1025, the defect itself: after a refresh that answered with a one-minute token, the next page
    /// redeemed the refresh token again. With rotating refresh tokens, two pages loading at once could then
    /// use the same one.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_OnTheNextPageAfterARefreshToAOneMinuteToken_DoesNotRefreshAgain()
    {
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);
        string firstRefreshedAccessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);

        ITokenEndpointClient tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
        tokenEndpointClient
            .RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                new TokenResponse
                {
                    AccessToken = firstRefreshedAccessToken,
                    RefreshToken = "first-rotated-refresh-token",
                    ExpiresIn = 60
                },
                new TokenResponse
                {
                    AccessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer),
                    RefreshToken = "second-rotated-refresh-token",
                    ExpiresIn = 60
                });
        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [signingKey],
            discoveryIssuer: DiscoveryIssuer,
            tokenEndpointClient: tokenEndpointClient);
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
    /// #1025: every session since the first sign-in already carries a refresh time, so each refresh has to
    /// move it forward. A refresh time left in the past would make every later page refresh again.
    /// </summary>
    [Theory]
    [InlineData(60, 30)]
    [InlineData(300, 240)]
    public async Task ValidateAsync_WhenARefreshReplacesAStoredRefreshTime_MovesItForwardAndTheNextPageKeepsTheToken(
        int expiresIn,
        int secondsUntilRefresh)
    {
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);
        string firstRefreshedAccessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);
        FakeTimeProvider time = new(Start);

        ITokenEndpointClient tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
        tokenEndpointClient
            .RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                new TokenResponse
                {
                    AccessToken = firstRefreshedAccessToken,
                    RefreshToken = "first-rotated-refresh-token",
                    ExpiresIn = expiresIn
                },
                new TokenResponse
                {
                    AccessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer),
                    RefreshToken = "second-rotated-refresh-token",
                    ExpiresIn = expiresIn
                });
        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [signingKey],
            discoveryIssuer: DiscoveryIssuer,
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
    /// #1025: the stored refresh time decides, not the last 60 seconds. A one-minute token is kept until
    /// half of it is gone, although it is inside its last minute from the start.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_InsideTheLastMinuteBeforeTheStoredRefreshTime_KeepsTheToken()
    {
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);

        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [signingKey],
            discoveryIssuer: DiscoveryIssuer,
            refreshResult: new TokenResponse
            {
                AccessToken = "refreshed-access-token",
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
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);

        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [signingKey],
            discoveryIssuer: DiscoveryIssuer,
            refreshResult: new TokenResponse
            {
                AccessToken = "refreshed-access-token",
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
        context.Properties.GetTokenValue(AccessTokenName).ShouldBe("refreshed-access-token");
        context.Properties.GetTokenValue(RefreshTokenName).ShouldBe("rotated-refresh-token");
    }

    /// <summary>
    /// #1025: a refresh time that cannot be read counts as none, so the token is refreshed in its last 60
    /// seconds, and the refresh stores a readable one in its place.
    /// </summary>
    [Theory]
    [InlineData("not-a-date")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ValidateAsync_InTheLastMinuteWithAnUnreadableStoredRefreshTime_RefreshesAndStoresAReadableOne(
        string storedRefreshAt)
    {
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);
        FakeTimeProvider time = new(Start);

        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [signingKey],
            discoveryIssuer: DiscoveryIssuer,
            refreshResult: new TokenResponse
            {
                AccessToken = "refreshed-access-token",
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

    /// <summary>
    /// An unreadable refresh time must not make the token due on every page either.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_BeforeTheLastMinuteWithAnUnreadableStoredRefreshTime_KeepsTheToken()
    {
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);
        FakeTimeProvider time = new(Start);

        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [signingKey],
            discoveryIssuer: DiscoveryIssuer,
            refreshResult: new TokenResponse
            {
                AccessToken = "refreshed-access-token",
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
    /// #1025: this code never writes a refresh time later than the expiry. If one is there anyway, trusting
    /// it would let the token run out before it is refreshed, so the last-minute rule applies.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_WhenTheStoredRefreshTimeIsLaterThanTheExpiry_RefreshesInTheLastMinute()
    {
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);
        FakeTimeProvider time = new(Start);

        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [signingKey],
            discoveryIssuer: DiscoveryIssuer,
            refreshResult: new TokenResponse
            {
                AccessToken = "refreshed-access-token",
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
    /// A cookie written before #1025 has no stored refresh time. It keeps the old rule and is refreshed in
    /// the last 60 seconds of its token, so the sessions that are already open carry on.
    /// </summary>
    [Theory]
    [InlineData(55, true)]
    [InlineData(65, false)]
    public async Task ValidateAsync_WhenTheCookieHasNoStoredRefreshTime_RefreshesInTheLastMinute(
        int secondsUntilExpiry,
        bool expectRefresh)
    {
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);

        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [signingKey],
            discoveryIssuer: DiscoveryIssuer,
            refreshResult: new TokenResponse
            {
                AccessToken = "refreshed-access-token",
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

    [Fact]
    public async Task ValidateAsync_WhenSessionMarkedDead_RejectsPrincipalAndSignsOut()
    {
        // An earlier 401 marked this subject dead. That check has to reject the session before any
        // refresh or signature check runs, even though the token is still valid and signed with a trusted
        // key.
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);

        IDeadSessionRegistry deadSessionRegistry = Substitute.For<IDeadSessionRegistry>();
        deadSessionRegistry
            .ConsumeAsync(Subject, Arg.Any<CancellationToken>())
            .Returns(true);

        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [signingKey],
            discoveryIssuer: DiscoveryIssuer,
            deadSessionRegistry: deadSessionRegistry);
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
    public async Task ValidateAsync_WhenSessionAliveAndTrusted_DoesNotRaiseExpiryNotice()
    {
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);

        ISessionExpiryNotice sessionExpiryNotice = Substitute.For<ISessionExpiryNotice>();
        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [signingKey],
            discoveryIssuer: DiscoveryIssuer,
            sessionExpiryNotice: sessionExpiryNotice);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken, authenticationService, expiresAt: DateTimeOffset.UtcNow.AddHours(1));

        await refresher.ValidateAsync(context);

        sessionExpiryNotice.DidNotReceive().Raise();
    }

    [Fact]
    public async Task ValidateAsync_WhenPrincipalRejected_RaisesOneShotExpiryNotice()
    {
        // A rotated key forces a rejection; the soft "session expired" notice must be raised so the next
        // render can surface the banner.
        RsaSecurityKey actualSigningKey = CreateRsaKey();
        RsaSecurityKey trustedKey = CreateRsaKey();
        string accessToken = MintAccessToken(actualSigningKey, tokenIssuer: DiscoveryIssuer);

        ISessionExpiryNotice sessionExpiryNotice = Substitute.For<ISessionExpiryNotice>();
        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [trustedKey],
            discoveryIssuer: DiscoveryIssuer,
            sessionExpiryNotice: sessionExpiryNotice);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken, authenticationService, expiresAt: DateTimeOffset.UtcNow.AddHours(1));

        await refresher.ValidateAsync(context);

        context.Principal.ShouldBeNull();
        sessionExpiryNotice.Received(1).Raise();
    }

    /// <summary>
    /// #964: the sign-out revokes the stored refresh token. A refresh first would redeem it, and OpenIddict
    /// still accepts a redeemed token for its reuse window, so the token in a copied cookie would survive.
    /// </summary>
    [Theory]
    [InlineData("/auth/logout")]
    [InlineData("/AUTH/Logout")]
    [InlineData("/auth/logout/")]
    public async Task ValidateAsync_OnTheSignOutRequestNearExpiry_KeepsTheStoredRefreshTokenUnredeemed(string path)
    {
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);

        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [signingKey],
            discoveryIssuer: DiscoveryIssuer,
            refreshResult: new TokenResponse
            {
                AccessToken = "refreshed-access-token",
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
    /// Only what routing sends to the logout handler skips the check. A GET to the same path ends on the
    /// 405 page, and skipping there would lose a dead-session marker and let the cookie's sliding renewal
    /// write back a token that is about to be redeemed.
    /// </summary>
    [Theory]
    [InlineData("POST", "/auth/logout-everywhere")]
    [InlineData("POST", "/auth/logoutx")]
    [InlineData("POST", "/auth/logout/extra")]
    [InlineData("POST", "/auth/login")]
    [InlineData("GET", "/auth/logout")]
    [InlineData("HEAD", "/auth/logout")]
    [InlineData("PUT", "/auth/logout")]
    public async Task ValidateAsync_OnARequestThatOnlyLooksLikeTheSignOut_StillRefreshes(string method, string path)
    {
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);

        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [signingKey],
            discoveryIssuer: DiscoveryIssuer,
            refreshResult: new TokenResponse
            {
                AccessToken = "refreshed-access-token",
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
    /// The sign-out ends the session itself, so a dead-session marker must not reject it first: that would
    /// hide the tokens from the sign-out and show the "session expired" notice to a user who signed out.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_OnTheSignOutRequestWhenMarkedDead_KeepsTheSessionForTheSignOut()
    {
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);

        IDeadSessionRegistry deadSessionRegistry = Substitute.For<IDeadSessionRegistry>();
        deadSessionRegistry.ConsumeAsync(Subject, Arg.Any<CancellationToken>()).Returns(true);
        ISessionExpiryNotice sessionExpiryNotice = Substitute.For<ISessionExpiryNotice>();
        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [signingKey],
            discoveryIssuer: DiscoveryIssuer,
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
    /// A marker left behind would end the user's next sign-in at once, so the sign-out still reads it, and
    /// a dropped request must not stop that read. Reading it is invisible in the result, hence the check.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_OnTheSignOutRequest_ClearsTheDeadSessionMarkerEvenWhenTheBrowserLeft()
    {
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);

        IDeadSessionRegistry deadSessionRegistry = Substitute.For<IDeadSessionRegistry>();
        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [signingKey],
            discoveryIssuer: DiscoveryIssuer,
            deadSessionRegistry: deadSessionRegistry);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: DateTimeOffset.UtcNow.AddHours(1),
            path: "/auth/logout",
            requestAborted: new CancellationToken(canceled: true),
            method: "POST");

        await refresher.ValidateAsync(context);

        await deadSessionRegistry.Received(1).ConsumeAsync(
            Subject, Arg.Is<CancellationToken>(token => !token.IsCancellationRequested));
    }

    /// <summary>
    /// Off the sign-out, a token signed by an unknown key is rejected and a dropped request throws. On the
    /// sign-out neither may happen, or the sign-out never reads the tokens it has to revoke.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_OnTheSignOutRequestAfterTheBrowserLeft_KeepsTheSessionWithoutChecks()
    {
        RsaSecurityKey actualSigningKey = CreateRsaKey();
        RsaSecurityKey trustedKey = CreateRsaKey();
        string accessToken = MintAccessToken(actualSigningKey, tokenIssuer: DiscoveryIssuer);

        CookieTokenRefresher refresher = CreateRefresher(trustedKeys: [trustedKey], discoveryIssuer: DiscoveryIssuer);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: DateTimeOffset.UtcNow.AddHours(1),
            refreshToken: "refresh-token",
            path: "/auth/logout",
            requestAborted: new CancellationToken(canceled: true),
            method: "POST");

        await refresher.ValidateAsync(context);

        context.Principal.ShouldNotBeNull();
    }

    public void Dispose()
    {
        foreach (RSA rsa in _rsaInstances)
        {
            rsa.Dispose();
        }
    }

    private CookieTokenRefresher CreateRefresher(
        IReadOnlyCollection<SecurityKey> trustedKeys,
        string? discoveryIssuer,
        TokenResponse? refreshResult = null,
        IDeadSessionRegistry? deadSessionRegistry = null,
        ISessionExpiryNotice? sessionExpiryNotice = null,
        ILogger<CookieTokenRefresher>? logger = null,
        ITokenEndpointClient? tokenEndpointClient = null,
        TimeProvider? timeProvider = null)
    {
        if (tokenEndpointClient is null)
        {
            tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
            if (refreshResult is not null)
            {
                tokenEndpointClient
                    .RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(refreshResult);
            }
        }

        OpenIdConnectConfiguration configuration = new();
        if (discoveryIssuer is not null)
        {
            configuration.Issuer = discoveryIssuer;
        }

        foreach (SecurityKey key in trustedKeys)
        {
            configuration.SigningKeys.Add(key);
        }

        OpenIdConnectOptions openIdConnectOptions = new()
        {
            ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration)
        };

        IOptionsMonitor<OpenIdConnectOptions> optionsMonitor =
            Substitute.For<IOptionsMonitor<OpenIdConnectOptions>>();
        optionsMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme).Returns(openIdConnectOptions);

        return new CookieTokenRefresher(
            tokenEndpointClient,
            optionsMonitor,
            deadSessionRegistry ?? Substitute.For<IDeadSessionRegistry>(),
            sessionExpiryNotice ?? Substitute.For<ISessionExpiryNotice>(),
            timeProvider ?? TimeProvider.System,
            logger ?? NullLogger<CookieTokenRefresher>.Instance);
    }

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
        List<AuthenticationToken> tokens =
        [
            new AuthenticationToken { Name = AccessTokenName, Value = accessToken },
            new AuthenticationToken
            {
                Name = ExpiresAtName,
                Value = expiresAt.ToString("o", CultureInfo.InvariantCulture)
            }
        ];

        if (refreshToken is not null)
        {
            tokens.Add(new AuthenticationToken { Name = RefreshTokenName, Value = refreshToken });
        }

        if (idToken is not null)
        {
            tokens.Add(new AuthenticationToken { Name = IdTokenName, Value = idToken });
        }

        string? storedRefreshAt = rawRefreshAt ?? refreshAt?.ToString("o", CultureInfo.InvariantCulture);
        if (storedRefreshAt is not null)
        {
            tokens.Add(new AuthenticationToken { Name = RefreshAtName, Value = storedRefreshAt });
        }

        AuthenticationProperties properties = new();
        properties.StoreTokens(tokens);

        return CreateContext(properties, authenticationService, path, requestAborted, method);
    }

    /// <summary>
    /// The next request of the same browser: its cookie carries the tokens an earlier request stored.
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

    private static string MintAccessToken(SecurityKey signingKey, string tokenIssuer)
    {
        SigningCredentials signingCredentials = new(signingKey, SecurityAlgorithms.RsaSha256);

        SecurityTokenDescriptor descriptor = new()
        {
            Issuer = tokenIssuer,
            Subject = new ClaimsIdentity([new Claim("sub", Subject)]),
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = signingCredentials
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private RsaSecurityKey CreateRsaKey()
    {
        RSA rsa = RSA.Create(2048);
        _rsaInstances.Add(rsa);
        return new RsaSecurityKey(rsa) { KeyId = Guid.NewGuid().ToString("N") };
    }
}
