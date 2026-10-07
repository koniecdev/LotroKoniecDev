using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
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
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Auth.TokenRefresh;

public sealed class CookieTokenRefresherTests : IDisposable
{
    private const string DiscoveryIssuer = "https://localhost:5003";
    private const string RevocationEndpoint = "https://localhost:5003/connect/revoke";
    private const string Subject = "11111111-1111-1111-1111-111111111111";
    private const string AccessTokenName = "access_token";
    private const string RefreshTokenName = "refresh_token";
    private const string IdTokenName = "id_token";
    private const string ExpiresAtName = "expires_at";

    /// <summary>
    /// The base64url of <c>{"alg":"RS256"}</c>. The fixtures below are joined from parts, so no line holds a
    /// whole token that a secret scanner would report.
    /// </summary>
    private const string Rs256Header = "eyJhbGciOiJSUzI1NiJ9";

    /// <summary>The base64url of <c>{"alg":"HS256"}</c>.</summary>
    private const string Hs256Header = "eyJhbGciOiJIUzI1NiJ9";

    /// <summary>
    /// A renewed token's signature is never checked, so these only need the shape of a compact JWS (#1028):
    /// the RS256 header, the payload <c>{"sub":"a"}</c> or <c>{"sub":"b"}</c>, and a dummy signature.
    /// </summary>
    private const string RefreshedJws = Rs256Header + ".eyJzdWIiOiJhIn0.c2lnbmF0dXJl";

    private const string RotatedIdJws = Rs256Header + ".eyJzdWIiOiJiIn0.c2lnbmF0dXJl";

    /// <summary>A real-time guard, so a broken time limit fails the test instead of hanging the run.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

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
                IdToken = RotatedIdJws,
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
                AccessToken = RefreshedJws,
                RefreshToken = "rotated-refresh-token",
                IdToken = RotatedIdJws,
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
    /// #1028: a renewed token skips the signature check, so a broken one would reach the API on this very
    /// page and be refused there. One with a line break would not even fit in the request header. None of
    /// the answer may be stored.
    /// </summary>
    [Theory]
    [InlineData("null")]
    [InlineData("<html><body>502 Bad Gateway</body></html>")]
    [InlineData(RefreshedJws + "\n")]
    [InlineData(RefreshedJws + "\r\n")]
    [InlineData(RefreshedJws + "\r")]
    [InlineData("\n" + RefreshedJws)]
    [InlineData(RefreshedJws + "\0")]
    [InlineData(RefreshedJws + " ")]
    [InlineData(" " + RefreshedJws)]
    [InlineData("\"" + RefreshedJws + "\"")]
    [InlineData("Bearer " + RefreshedJws)]
    [InlineData("aGVhZGVy.cGF5bG9hZA")]
    [InlineData(RefreshedJws + ".c2lnbmF0dXJl")]
    [InlineData("aGVhZGVy.a2V5.aXY.Y2lwaGVy.dGFn")]
    [InlineData("aGVhZGVy..c2lnbmF0dXJl")]
    [InlineData(".cGF5bG9hZA.c2lnbmF0dXJl")]
    [InlineData(Rs256Header + ".eyJzdWIiOiJhIn0.")]
    [InlineData("..")]
    [InlineData(RefreshedJws + "=")]
    [InlineData(Rs256Header + ".eyJzdWIiOiJhIn0.c2ln+bmF0/dXJl")]
    [InlineData(Rs256Header + ".eyJzdWIiOiJhIn0.c2ln\tbmF0dXJl")]
    [InlineData(Rs256Header + ".eyJzdWIiOiJhIn0.c2lnbmF0dXJë")]
    [InlineData("a.b.c")]
    [InlineData("aGVhZGVy.cmVmcmVzaGVk.c2lnbmF0dXJl")]
    [InlineData(Rs256Header + "a.eyJzdWIiOiJhIn0.c2lnbmF0dXJl")]
    [InlineData("W10.eyJzdWIiOiJhIn0.c2lnbmF0dXJl")]
    [InlineData(Rs256Header + ".W10.c2lnbmF0dXJl")]
    [InlineData("e30.eyJzdWIiOiJhIn0.c2lnbmF0dXJl")]
    [InlineData(Rs256Header + ".eyJzdWIiOiJhIn0.c2ln\u200BbmF0dXJl")]
    [InlineData(RefreshedJws + "\u00A0")]
    public async Task ValidateAsync_WhenRefreshAnswersWithAMalformedAccessToken_RejectsPrincipalAndStoresNothing(
        string malformedAccessToken)
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
                AccessToken = malformedAccessToken,
                RefreshToken = "rotated-refresh-token",
                IdToken = RotatedIdJws,
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
    /// #1028: a broken ID token means the answer was damaged on the way, so its access token is not trusted
    /// either. Kept, the broken ID token would be the sign-out's hint, and that sign-out would end nothing on
    /// the sign-in server.
    /// </summary>
    [Theory]
    [InlineData("null")]
    [InlineData("<html><body>502 Bad Gateway</body></html>")]
    [InlineData(RotatedIdJws + "\n")]
    [InlineData(RotatedIdJws + "\r\n")]
    [InlineData(RotatedIdJws + "\0")]
    [InlineData("aGVhZGVy.cGF5bG9hZA")]
    [InlineData("aGVhZGVy.a2V5.aXY.Y2lwaGVy.dGFn")]
    [InlineData(Rs256Header + ".eyJzdWIiOiJiIn0.")]
    [InlineData("a.b.c")]
    [InlineData("e30.eyJzdWIiOiJiIn0.c2lnbmF0dXJl")]
    public async Task ValidateAsync_WhenRefreshAnswersWithAMalformedIdToken_RejectsPrincipalAndStoresNothing(
        string malformedIdToken)
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
                AccessToken = RefreshedJws,
                RefreshToken = "rotated-refresh-token",
                IdToken = malformedIdToken,
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
    /// #1028: a refresh token is opaque to the client, but RFC 6749 Appendix A.17 allows only printable
    /// ASCII in it. One with a line break or another control character means the answer was damaged.
    /// </summary>
    [Theory]
    [InlineData("rotated-refresh-token\n")]
    [InlineData("rotated-refresh-token\r\n")]
    [InlineData("\rrotated-refresh-token")]
    [InlineData("rotated\trefresh-token")]
    [InlineData("rotated-refresh-tökén")]
    [InlineData("rotated-refresh-token\u007f")]
    [InlineData("rotated-refresh-token\0")]
    public async Task ValidateAsync_WhenRefreshAnswersWithAMalformedRefreshToken_RejectsPrincipalAndStoresNothing(
        string malformedRefreshToken)
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
                AccessToken = RefreshedJws,
                RefreshToken = malformedRefreshToken,
                IdToken = RotatedIdJws,
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
    /// #1028 checks the shape, never the signature: a header that names an algorithm and a JSON payload pass,
    /// whatever the signature part holds, down to one character. The refresh token is opaque, so any
    /// printable ASCII passes, a space and a five-part JWE included.
    /// </summary>
    [Theory]
    [InlineData(Hs256Header + ".e30.x", Hs256Header + ".e30.x", "opaque refresh token")]
    [InlineData(Hs256Header + ".e30.A-_z09", RotatedIdJws, "aGVhZGVy.a2V5.aXY.Y2lwaGVy.dGFn")]
    [InlineData(RefreshedJws, RotatedIdJws, " !~ ")]
    public async Task ValidateAsync_WhenRefreshAnswersWithWellFormedTokens_StoresTheAnswer(
        string refreshedAccessToken,
        string refreshedIdToken,
        string refreshedRefreshToken)
    {
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);

        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [signingKey],
            discoveryIssuer: DiscoveryIssuer,
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
    /// A production-shaped answer must pass, or every renewal would sign the user out: a real JWT access
    /// token and ID token, signed by a key the website has not fetched yet, and a reference refresh token
    /// like OpenIddict's (43 base64url characters).
    /// </summary>
    [Fact]
    public async Task ValidateAsync_WhenRefreshAnswerIsShapedLikeOurSignInServers_StoresTheAnswer()
    {
        RsaSecurityKey trustedKey = CreateRsaKey();
        RsaSecurityKey freshUpstreamKey = CreateRsaKey();
        string staleAccessToken = MintAccessToken(trustedKey, tokenIssuer: DiscoveryIssuer);
        string refreshedAccessToken = MintAccessToken(freshUpstreamKey, tokenIssuer: DiscoveryIssuer);
        string refreshedIdToken = MintAccessToken(freshUpstreamKey, tokenIssuer: DiscoveryIssuer);
        string referenceRefreshToken = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [trustedKey],
            discoveryIssuer: DiscoveryIssuer,
            refreshResult: new TokenResponse
            {
                AccessToken = refreshedAccessToken,
                RefreshToken = referenceRefreshToken,
                IdToken = refreshedIdToken,
                ExpiresIn = 3600
            });
        CookieValidatePrincipalContext context = CreateContext(
            staleAccessToken,
            authenticationService,
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(30),
            refreshToken: "refresh-token",
            idToken: "id-token");

        await refresher.ValidateAsync(context);

        context.Principal.ShouldNotBeNull();
        context.Properties.GetTokenValue(AccessTokenName).ShouldBe(refreshedAccessToken);
        context.Properties.GetTokenValue(RefreshTokenName).ShouldBe(referenceRefreshToken);
        context.Properties.GetTokenValue(IdTokenName).ShouldBe(refreshedIdToken);
    }

    /// <summary>
    /// #1028: a token from the sign-in itself, not from a renewal, gets no shape check. The signature check
    /// on the next request refuses one with a trailing line break before a page puts it in a request header,
    /// where it would end the page with an error. That check needs the cookie's expiry time, and our sign-in
    /// server always sends one.
    /// </summary>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public async Task ValidateAsync_WithUnexpiredTrustedTokenEndingInALineBreak_RejectsPrincipal(string lineBreak)
    {
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer) + lineBreak;

        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        CookieTokenRefresher refresher = CreateRefresher(trustedKeys: [signingKey], discoveryIssuer: DiscoveryIssuer);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken, authenticationService, expiresAt: DateTimeOffset.UtcNow.AddHours(1));

        await refresher.ValidateAsync(context);

        context.Principal.ShouldBeNull();
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
    [InlineData(RefreshedJws, null, "expires_in: missing")]
    [InlineData(RefreshedJws, 0, "expires_in: 0")]
    [InlineData(RefreshedJws, -5, "expires_in: -5")]
    [InlineData("null", 300, "access_token that is not a compact JWS")]
    [InlineData(RefreshedJws + "\n", 300, "access_token that is not a compact JWS")]
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

    [Theory]
    [InlineData("rotated-refresh-token\n", RotatedIdJws, "refresh_token with characters outside printable ASCII")]
    [InlineData("rotated-refresh-token", "null", "id_token that is not a compact JWS")]
    public async Task ValidateAsync_WhenRefreshAnswerCarriesABrokenRefreshOrIdToken_LogsOneWarningWithTheReason(
        string refreshedRefreshToken,
        string refreshedIdToken,
        string expectedReason)
    {
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);

        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [signingKey],
            discoveryIssuer: DiscoveryIssuer,
            refreshResult: new TokenResponse
            {
                AccessToken = RefreshedJws,
                RefreshToken = refreshedRefreshToken,
                IdToken = refreshedIdToken,
                ExpiresIn = 300
            },
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
    [InlineData(RefreshedJws, null)]
    [InlineData(RefreshedJws, 0)]
    [InlineData("null", 300)]
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

    [Theory]
    [InlineData("rotated-refresh-token\n", RotatedIdJws)]
    [InlineData("rotated-refresh-token", "null")]
    public async Task ValidateAsync_WhenRefreshAnswerCarriesABrokenRefreshOrIdToken_RaisesTheExpiryNotice(
        string refreshedRefreshToken,
        string refreshedIdToken)
    {
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);

        ISessionExpiryNotice sessionExpiryNotice = Substitute.For<ISessionExpiryNotice>();
        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [signingKey],
            discoveryIssuer: DiscoveryIssuer,
            refreshResult: new TokenResponse
            {
                AccessToken = RefreshedJws,
                RefreshToken = refreshedRefreshToken,
                IdToken = refreshedIdToken,
                ExpiresIn = 300
            },
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
                AccessToken = RefreshedJws,
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
        context.Properties.GetTokenValue(AccessTokenName).ShouldBe(RefreshedJws);
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
                AccessToken = RefreshedJws,
                RefreshToken = "rotated-refresh-token",
                IdToken = RotatedIdJws,
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
        context.Properties.GetTokenValue(AccessTokenName).ShouldBe(RefreshedJws);
        context.Properties.GetTokenValue(RefreshTokenName).ShouldBe("rotated-refresh-token");
        context.Properties.GetTokenValue(IdTokenName).ShouldBe(RotatedIdJws);
        DateTimeOffset storedExpiresAt = DateTimeOffset.Parse(
            context.Properties.GetTokenValue(ExpiresAtName).ShouldNotBeNull(), CultureInfo.InvariantCulture);
        storedExpiresAt.ShouldBeInRange(before.AddSeconds(expiresIn), after.AddSeconds(expiresIn));
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
    /// The local sign-out revokes it too, so the same holds there (#1027).
    /// </summary>
    [Theory]
    [InlineData("/auth/logout")]
    [InlineData("/AUTH/Logout")]
    [InlineData("/auth/logout/")]
    [InlineData("/auth/local-signout")]
    [InlineData("/auth/local-signout/")]
    public async Task ValidateAsync_OnTheSignOutRequestNearExpiry_KeepsTheStoredRefreshTokenUnredeemed(string path)
    {
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);

        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [signingKey],
            discoveryIssuer: DiscoveryIssuer,
            refreshResult: new TokenResponse
            {
                AccessToken = RefreshedJws,
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
    [InlineData("POST", "/auth/local-signoutx")]
    [InlineData("GET", "/auth/local-signout")]
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
                AccessToken = RefreshedJws,
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

    /// <summary>
    /// #1027: deleting the cookie alone leaves its refresh token valid at the auth server for hours, so a copy
    /// of the cookie could keep renewing. A key change upstream ends the session, so it revokes the token.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_WithUnexpiredTokenSignedByRotatedKey_RevokesTheStoredRefreshToken()
    {
        RsaSecurityKey actualSigningKey = CreateRsaKey();
        RsaSecurityKey trustedKey = CreateRsaKey();
        string accessToken = MintAccessToken(actualSigningKey, tokenIssuer: DiscoveryIssuer);

        ITokenEndpointClient tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [trustedKey], discoveryIssuer: DiscoveryIssuer, tokenEndpointClient: tokenEndpointClient);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: DateTimeOffset.UtcNow.AddHours(1),
            refreshToken: "refresh-token");

        await refresher.ValidateAsync(context);

        RevokedRefreshTokens(tokenEndpointClient).ShouldBe(["refresh-token"]);
    }

    /// <summary>
    /// #1027: the 401 may come from the TMS API, which checks the access token alone and revokes nothing, so
    /// the refresh token can still be valid. When the auth server has already revoked it, the revoke does no
    /// harm: OpenIddict answers 200 for a token that is no longer valid. The session is ended for a real
    /// reason here, so a browser that left does not stop the revoke.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidateAsync_WhenSessionMarkedDead_RevokesTheStoredRefreshToken(bool browserLeft)
    {
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);

        IDeadSessionRegistry deadSessionRegistry = Substitute.For<IDeadSessionRegistry>();
        deadSessionRegistry.ConsumeAsync(Subject, Arg.Any<CancellationToken>()).Returns(true);
        ITokenEndpointClient tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [signingKey],
            discoveryIssuer: DiscoveryIssuer,
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
    /// #1027: the auth server has already swapped the stored refresh token for the one in the answer. The
    /// swapped one is still accepted for OpenIddict's reuse window, and the new one for hours, so both are
    /// revoked. Revoking only the stored one leaves the new one valid (checked against OpenIddict 7.7.1).
    /// </summary>
    [Theory]
    [InlineData(null, 300)]
    [InlineData("   ", 300)]
    [InlineData(RefreshedJws, null)]
    [InlineData(RefreshedJws, 0)]
    [InlineData("null", 300)]
    [InlineData(RefreshedJws + "\n", 300)]
    public async Task ValidateAsync_WhenRefreshAnswerIsUnusable_RevokesTheStoredAndTheReceivedRefreshToken(
        string? refreshedAccessToken,
        int? expiresIn)
    {
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);

        ITokenEndpointClient tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [signingKey],
            discoveryIssuer: DiscoveryIssuer,
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
    /// #1028 refuses an answer for a broken refresh or ID token too, and the auth server has swapped the
    /// stored token there as well (#1027). A broken refresh token is still sent to be revoked: the auth server
    /// answers an unknown token without an error (RFC 7009 §2.2).
    /// </summary>
    [Theory]
    [InlineData("rotated-refresh-token\n", RotatedIdJws)]
    [InlineData("rotated-refresh-token", "null")]
    public async Task ValidateAsync_WhenRefreshAnswerCarriesABrokenRefreshOrIdToken_RevokesTheStoredAndTheReceivedRefreshToken(
        string refreshedRefreshToken,
        string refreshedIdToken)
    {
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);

        ITokenEndpointClient tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [signingKey],
            discoveryIssuer: DiscoveryIssuer,
            refreshResult: new TokenResponse
            {
                AccessToken = RefreshedJws,
                RefreshToken = refreshedRefreshToken,
                IdToken = refreshedIdToken,
                ExpiresIn = 300
            },
            tokenEndpointClient: tokenEndpointClient);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(30),
            refreshToken: "refresh-token");

        await refresher.ValidateAsync(context);

        RevokedRefreshTokens(tokenEndpointClient).ShouldBe(["refresh-token", refreshedRefreshToken], ignoreOrder: true);
    }

    /// <summary>
    /// An answer with no refresh token, a blank one or the stored one again leaves only the stored token to
    /// revoke, and that one is revoked once.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("refresh-token")]
    public async Task ValidateAsync_WhenUnusableRefreshAnswerCarriesNoNewRefreshToken_RevokesTheStoredOneOnce(
        string? receivedRefreshToken)
    {
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);

        ITokenEndpointClient tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [signingKey],
            discoveryIssuer: DiscoveryIssuer,
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
    /// #1027: a refused refresh is not always a dead token. A lockout, for one, revokes nothing, and the
    /// token works again once the lockout ends.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_WhenNearExpiryAndRefreshFails_RevokesTheStoredRefreshToken()
    {
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);

        ITokenEndpointClient tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [signingKey], discoveryIssuer: DiscoveryIssuer, tokenEndpointClient: tokenEndpointClient);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(30),
            refreshToken: "refresh-token");

        await refresher.ValidateAsync(context);

        RevokedRefreshTokens(tokenEndpointClient).ShouldBe(["refresh-token"]);
    }

    /// <summary>
    /// A browser that leaves during the refresh makes the token client answer null, but the token is not
    /// dead. The browser never gets the deleted cookie and sends the same refresh token on its next request,
    /// so a revoke here would sign out a user who only clicked a second link.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_WhenTheBrowserLeavesDuringTheRefresh_KeepsTheStoredRefreshTokenAlive()
    {
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);

        ITokenEndpointClient tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [signingKey], discoveryIssuer: DiscoveryIssuer, tokenEndpointClient: tokenEndpointClient);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(30),
            refreshToken: "refresh-token",
            requestAborted: new CancellationToken(canceled: true));

        await refresher.ValidateAsync(context);

        RevokedRefreshTokens(tokenEndpointClient).ShouldBeEmpty();
    }

    [Fact]
    public async Task ValidateAsync_WhenNearExpiryWithoutRefreshToken_RevokesNothing()
    {
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);

        ITokenEndpointClient tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [signingKey], discoveryIssuer: DiscoveryIssuer, tokenEndpointClient: tokenEndpointClient);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(30),
            refreshToken: null);

        await refresher.ValidateAsync(context);

        RevokedRefreshTokens(tokenEndpointClient).ShouldBeEmpty();
    }

    /// <summary>
    /// The revoke runs only when a session ends, so a normal page load pays no extra call to the auth
    /// server (#923, #940).
    /// </summary>
    [Fact]
    public async Task ValidateAsync_WhenSessionAliveAndTrusted_RevokesNothing()
    {
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);

        ITokenEndpointClient tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [signingKey], discoveryIssuer: DiscoveryIssuer, tokenEndpointClient: tokenEndpointClient);
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
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);

        ITokenEndpointClient tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [signingKey],
            discoveryIssuer: DiscoveryIssuer,
            refreshResult: new TokenResponse
            {
                AccessToken = RefreshedJws,
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
    /// #1027: a stuck auth server never holds the page longer than the sign-out's own limit, even with two
    /// tokens to revoke.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_WhenTheAuthServerNeverAnswersTheRevokes_HoldsTheRequestNoLongerThanTheSignOutTimeLimit()
    {
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);

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
            trustedKeys: [signingKey],
            discoveryIssuer: DiscoveryIssuer,
            refreshResult: new TokenResponse { AccessToken = null, RefreshToken = "rotated-refresh-token", ExpiresIn = 300 },
            tokenEndpointClient: tokenEndpointClient,
            timeProvider: time);
        CookieValidatePrincipalContext context = CreateContext(
            accessToken,
            Substitute.For<IAuthenticationService>(),
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(30),
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
    /// The revoke is best effort: whatever it hits, the session still ends and no exception reaches the
    /// cookie handler, which would turn it into an error page.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_WhenTheRevokeThrows_StillEndsTheSession()
    {
        RsaSecurityKey signingKey = CreateRsaKey();
        string accessToken = MintAccessToken(signingKey, tokenIssuer: DiscoveryIssuer);

        IDeadSessionRegistry deadSessionRegistry = Substitute.For<IDeadSessionRegistry>();
        deadSessionRegistry.ConsumeAsync(Subject, Arg.Any<CancellationToken>()).Returns(true);
        ITokenEndpointClient tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
        tokenEndpointClient
            .RevokeRefreshTokenAsync(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("A new handler failed.")));
        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        CookieTokenRefresher refresher = CreateRefresher(
            trustedKeys: [signingKey],
            discoveryIssuer: DiscoveryIssuer,
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
        tokenEndpointClient ??= Substitute.For<ITokenEndpointClient>();
        if (refreshResult is not null)
        {
            tokenEndpointClient
                .RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(refreshResult);
        }

        OpenIdConnectConfiguration configuration = new() { RevocationEndpoint = RevocationEndpoint };
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

        RefreshTokenRevoker refreshTokenRevoker = new(
            tokenEndpointClient,
            optionsMonitor,
            timeProvider ?? TimeProvider.System,
            NullLogger<RefreshTokenRevoker>.Instance);

        return new CookieTokenRefresher(
            tokenEndpointClient,
            optionsMonitor,
            deadSessionRegistry ?? Substitute.For<IDeadSessionRegistry>(),
            sessionExpiryNotice ?? Substitute.For<ISessionExpiryNotice>(),
            refreshTokenRevoker,
            logger ?? NullLogger<CookieTokenRefresher>.Instance);
    }

    /// <summary>
    /// The refresh tokens sent to the auth server's revocation endpoint. A revoke is not visible in the
    /// result, so the calls to the token client, the boundary to the auth server, are read instead.
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

        AuthenticationProperties properties = new();
        properties.StoreTokens(tokens);

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
