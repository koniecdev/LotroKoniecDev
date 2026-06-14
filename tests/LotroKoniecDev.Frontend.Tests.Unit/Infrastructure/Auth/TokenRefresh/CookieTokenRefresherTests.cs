using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;
using LotroKoniecDev.Frontend.Infrastructure.Auth.TokenRefresh;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
    private const string ExpiresAtName = "expires_at";

    private readonly List<RSA> _rsaInstances = [];

    [Fact]
    public async Task ValidateAsync_WithUnexpiredTokenSignedByRotatedKey_RejectsPrincipalAndSignsOut()
    {
        // The token is alive on the local clock but its signature does not verify against the only key
        // the FE currently trusts — the upstream key has rotated.
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
        // Discovery has not yielded an issuer yet. The empty-issuer guard must skip the proactive check
        // rather than falsely log the user out — even with a key the FE doesn't trust.
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
        // A token inside the 60s pre-expiry skew is refreshed. The fresh token is signed by a key the FE
        // does not (yet) trust — the momentarily-stale-JWKS window. Because the token was just minted
        // upstream, the proactive probe must be skipped: the session survives and the token is stored.
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
    public async Task ValidateAsync_WhenSessionMarkedDead_RejectsPrincipalAndSignsOut()
    {
        // A prior 401 marked this subject dead. The reactive backstop must reject before any refresh or
        // proactive probe runs — even though the token is alive and signed by a trusted key.
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
        ISessionExpiryNotice? sessionExpiryNotice = null)
    {
        ITokenEndpointClient tokenEndpointClient = Substitute.For<ITokenEndpointClient>();
        if (refreshResult is not null)
        {
            tokenEndpointClient
                .RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(refreshResult);
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
            NullLogger<CookieTokenRefresher>.Instance);
    }

    private static CookieValidatePrincipalContext CreateContext(
        string accessToken,
        IAuthenticationService authenticationService,
        DateTimeOffset expiresAt,
        string? refreshToken = null)
    {
        ServiceCollection services = new();
        services.AddSingleton(authenticationService);

        DefaultHttpContext httpContext = new()
        {
            RequestServices = services.BuildServiceProvider()
        };

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
