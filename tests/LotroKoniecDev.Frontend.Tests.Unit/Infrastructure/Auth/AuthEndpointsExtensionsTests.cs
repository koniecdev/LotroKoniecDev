using LotroKoniecDev.Frontend.Infrastructure.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using NSubstitute;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Auth;

/// <summary>
/// Drives the login route's request delegate directly (no web host): when the OIDC authority is
/// reachable it must produce the OIDC challenge (302), but when the discovery fetch fails — the auth
/// server is down — it must warm-fail into a 503 so the status-code pages serve the friendly
/// "login unavailable" page instead of letting the challenge bubble a raw 500 (#311).
/// </summary>
public sealed class AuthEndpointsExtensionsTests
{
    public static TheoryData<Exception> AuthorityUnreachableFailures() => new()
    {
        new InvalidOperationException("IDX20803: Unable to obtain configuration from the authority."),
        new HttpRequestException("Connection refused."),
        new TaskCanceledException("The discovery request timed out."),
    };

    [Theory]
    [MemberData(nameof(AuthorityUnreachableFailures))]
    public async Task LoginAsync_WhenDiscoveryFetchFails_ReturnsServiceUnavailableInsteadOfRaw500(
        Exception discoveryFailure)
    {
        IConfigurationManager<OpenIdConnectConfiguration> configurationManager =
            Substitute.For<IConfigurationManager<OpenIdConnectConfiguration>>();
        configurationManager.GetConfigurationAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<OpenIdConnectConfiguration>(discoveryFailure));

        IResult result = await AuthEndpointsExtensions.LoginAsync(
            new DefaultHttpContext(),
            "/dashboard",
            CreateOptionsMonitor(configurationManager),
            NullLoggerFactory.Instance);

        StatusCodeHttpResult statusResult = result.ShouldBeOfType<StatusCodeHttpResult>();
        statusResult.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task LoginAsync_WhenAuthorityReachable_ReturnsTheOidcChallenge()
    {
        IResult result = await AuthEndpointsExtensions.LoginAsync(
            new DefaultHttpContext(),
            "/dashboard",
            CreateOptionsMonitor(CreateReachableConfigurationManager()),
            NullLoggerFactory.Instance);

        ChallengeHttpResult challenge = result.ShouldBeOfType<ChallengeHttpResult>();
        challenge.AuthenticationSchemes.ShouldHaveSingleItem()
            .ShouldBe(OpenIdConnectDefaults.AuthenticationScheme);
    }

    [Theory]
    [InlineData("/dashboard", "/dashboard")]
    [InlineData("/translations?status=NeedsReview", "/translations?status=NeedsReview")]
    [InlineData("https://evil.example.com/harvest", "/")]
    [InlineData("//evil.example.com", "/")]
    [InlineData("/\\evil.example.com", "/")]
    [InlineData("", "/")]
    [InlineData(null, "/")]
    public async Task LoginAsync_WhenAuthorityReachable_SanitizesReturnUrlToALocalRedirect(
        string? returnUrl, string expectedRedirectUri)
    {
        IResult result = await AuthEndpointsExtensions.LoginAsync(
            new DefaultHttpContext(),
            returnUrl,
            CreateOptionsMonitor(CreateReachableConfigurationManager()),
            NullLoggerFactory.Instance);

        ChallengeHttpResult challenge = result.ShouldBeOfType<ChallengeHttpResult>();
        challenge.Properties.ShouldNotBeNull();
        challenge.Properties!.RedirectUri.ShouldBe(expectedRedirectUri);
    }

    [Fact]
    public async Task LoginAsync_WhenNoConfigurationManager_StillReturnsTheOidcChallenge()
    {
        // Once the OIDC handler post-configures, ConfigurationManager is always set; the null-guard
        // mirrors CookieTokenRefresher so a missing manager can never turn a login into a false 503.
        IResult result = await AuthEndpointsExtensions.LoginAsync(
            new DefaultHttpContext(),
            "/dashboard",
            CreateOptionsMonitor(configurationManager: null),
            NullLoggerFactory.Instance);

        result.ShouldBeOfType<ChallengeHttpResult>();
    }

    [Fact]
    public async Task LocalSignOutAsync_SignsOutTheCookieAndRedirectsToTheLocalReturnUrl()
    {
        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        HttpContext context = CreateContextWith(authenticationService);

        IResult result = await AuthEndpointsExtensions.LocalSignOutAsync(
            context,
            "/account/deletion-scheduled?until=2026-07-25T10%3A00%3A00Z");

        RedirectHttpResult redirect = result.ShouldBeOfType<RedirectHttpResult>();
        redirect.Url.ShouldBe("/account/deletion-scheduled?until=2026-07-25T10%3A00%3A00Z");
        // Cookie sign-out is invisible in the return value — the .Received() is the only observable proof.
        await authenticationService.Received(1).SignOutAsync(
            context,
            CookieAuthenticationDefaults.AuthenticationScheme,
            Arg.Any<AuthenticationProperties?>());
    }

    [Theory]
    [InlineData("https://evil.example.com/harvest")]
    [InlineData("//evil.example.com")]
    [InlineData("/\\evil.example.com")]
    [InlineData("")]
    [InlineData(null)]
    public async Task LocalSignOutAsync_WhenReturnUrlIsNotLocal_RedirectsHome(string? returnUrl)
    {
        HttpContext context = CreateContextWith(Substitute.For<IAuthenticationService>());

        IResult result = await AuthEndpointsExtensions.LocalSignOutAsync(context, returnUrl);

        RedirectHttpResult redirect = result.ShouldBeOfType<RedirectHttpResult>();
        redirect.Url.ShouldBe("/");
    }

    private static HttpContext CreateContextWith(IAuthenticationService authenticationService)
    {
        ServiceCollection services = new();
        services.AddSingleton(authenticationService);
        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider()
        };
    }

    private static IOptionsMonitor<OpenIdConnectOptions> CreateOptionsMonitor(
        IConfigurationManager<OpenIdConnectConfiguration>? configurationManager)
    {
        IOptionsMonitor<OpenIdConnectOptions> optionsMonitor =
            Substitute.For<IOptionsMonitor<OpenIdConnectOptions>>();
        optionsMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme)
            .Returns(new OpenIdConnectOptions { ConfigurationManager = configurationManager });
        return optionsMonitor;
    }

    private static IConfigurationManager<OpenIdConnectConfiguration> CreateReachableConfigurationManager()
    {
        IConfigurationManager<OpenIdConnectConfiguration> configurationManager =
            Substitute.For<IConfigurationManager<OpenIdConnectConfiguration>>();
        configurationManager.GetConfigurationAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new OpenIdConnectConfiguration()));
        return configurationManager;
    }
}
