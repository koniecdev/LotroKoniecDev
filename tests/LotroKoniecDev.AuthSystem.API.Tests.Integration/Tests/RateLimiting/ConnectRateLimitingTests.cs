using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenIddict.Server;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.RateLimiting;

/// <summary>
/// Proves the brute-force limiter genuinely binds to the OpenIddict <c>/connect/*</c> endpoints
/// (#347): they are mapped at the root, so the policy must ride the group they are actually mapped
/// through — a bare <c>MapGroup("/connect")</c> convention never reaches them. Enforcement order
/// matters too: the limiter middleware must run BEFORE authentication, because OpenIddict validates
/// protocol requests inside the authentication stage and short-circuits invalid ones — a limiter
/// placed after it would never count exactly the junk traffic it exists to stop. Introspection and
/// revocation have no ASP.NET Core passthrough at all — OpenIddict middleware serves them entirely —
/// so their limits ride metadata-only routes (#349, <c>MiddlewareServedEndpoints</c>). The suite's
/// Testing host keeps the limiter middleware off; the burst tests force-arm it on a derived host to
/// observe real 429 rejection, which a Staging-environment factory cannot do in-suite (outside
/// Dev/Testing the settings validators demand production key material at startup).
/// </summary>
public sealed class ConnectRateLimitingTests : EndpointsTestBase
{
    private const string AuthEndpointRateLimitPolicy = "auth-endpoint-limit";

    /// <summary>Mirrors the auth-endpoint-limit policy: 10 permits per minute per client IP.</summary>
    private const int AuthEndpointPermitLimit = 10;

    public ConnectRateLimitingTests(AuthSystemApiFactory appFactory) : base(appFactory)
    {
    }

    [Theory]
    [InlineData("connect/token")]
    [InlineData("connect/authorize")]
    [InlineData("connect/logout")]
    [InlineData("connect/revoke")]
    [InlineData("connect/introspect")]
    [InlineData("connect/userinfo")]
    public void ConnectEndpoint_Always_CarriesAuthEndpointRateLimitPolicy(string route)
    {
        // Arrange
        EndpointDataSource endpointDataSource = Factory.Services.GetRequiredService<EndpointDataSource>();

        // Act
        RouteEndpoint endpoint = endpointDataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Single(routeEndpoint => routeEndpoint.RoutePattern.RawText?.TrimStart('/') == route);

        // Assert
        EnableRateLimitingAttribute? rateLimiting = endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>();
        rateLimiting.ShouldNotBeNull($"'{route}' must carry the brute-force rate-limit policy");
        rateLimiting.PolicyName.ShouldBe(AuthEndpointRateLimitPolicy);
    }

    [Fact]
    public void OpenIddictEndpointUri_Always_HasARoutedEndpointCarryingARateLimitPolicy()
    {
        // Arrange — introspection and revocation have NO ASP.NET Core passthrough in OpenIddict:
        // the middleware serves them entirely inside the authentication stage, so the ONLY thing
        // arming the limiter on such a URI is a metadata-carrying routed endpoint (#349,
        // MiddlewareServedEndpoints). This pins every auth-surface URI registered on the OpenIddict
        // server to such a carrier, so a URI added or renamed in OpenIddictExtensions without a
        // matching route fails here instead of silently shipping without a brute-force limit.
        // Discovery and JWKS are deliberately excluded: they process no credentials, and limiting
        // them would throttle every relying party's metadata refresh.
        OpenIddictServerOptions serverOptions = Factory.Services
            .GetRequiredService<IOptionsMonitor<OpenIddictServerOptions>>().CurrentValue;

        // The last three collections are empty today (no such URIs are registered) — enumerating
        // them now means enabling device/PAR/end-user-verification later cannot slip past this pin.
        List<Uri> authSurfaceUris = serverOptions.TokenEndpointUris
            .Concat(serverOptions.AuthorizationEndpointUris)
            .Concat(serverOptions.UserInfoEndpointUris)
            .Concat(serverOptions.IntrospectionEndpointUris)
            .Concat(serverOptions.RevocationEndpointUris)
            .Concat(serverOptions.EndSessionEndpointUris)
            .Concat(serverOptions.DeviceAuthorizationEndpointUris)
            .Concat(serverOptions.PushedAuthorizationEndpointUris)
            .Concat(serverOptions.EndUserVerificationEndpointUris)
            .ToList();

        EndpointDataSource endpointDataSource = Factory.Services.GetRequiredService<EndpointDataSource>();

        // Act & Assert
        authSurfaceUris.ShouldNotBeEmpty();
        foreach (Uri authSurfaceUri in authSurfaceUris)
        {
            string route = authSurfaceUri.OriginalString.TrimStart('/');
            List<RouteEndpoint> carriers = endpointDataSource.Endpoints
                .OfType<RouteEndpoint>()
                .Where(routeEndpoint => routeEndpoint.RoutePattern.RawText?.TrimStart('/') == route)
                .ToList();

            carriers.ShouldNotBeEmpty(
                $"OpenIddict serves '{route}' but no routed endpoint matches it — without a metadata "
                + "carrier (see MiddlewareServedEndpoints) the brute-force limiter never engages");
            carriers.ShouldAllBe(
                carrier => carrier.Metadata.GetMetadata<EnableRateLimitingAttribute>() != null,
                $"every routed endpoint for '{route}' must carry a rate-limit policy");
        }
    }

    [Theory]
    [InlineData("auth/forgot-password", "forgot-password-limit")]
    [InlineData("auth/resend-email-confirmation", "resend-confirmation-limit")]
    public void ApiEndpointWithOwnPolicy_Always_OverridesTheGroupPolicy(string route, string expectedPolicy)
    {
        // Arrange — group metadata is attached in every environment now, so the endpoint-level
        // policy must keep winning (GetMetadata returns the last match: group first, endpoint last).
        EndpointDataSource endpointDataSource = Factory.Services.GetRequiredService<EndpointDataSource>();

        // Act
        RouteEndpoint endpoint = endpointDataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Single(routeEndpoint => routeEndpoint.RoutePattern.RawText?.TrimStart('/') == route);

        // Assert
        EnableRateLimitingAttribute? rateLimiting = endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>();
        rateLimiting.ShouldNotBeNull();
        rateLimiting.PolicyName.ShouldBe(expectedPolicy);
    }

    [Theory]
    [InlineData("health")]
    [InlineData("health/live")]
    [InlineData("health/ready")]
    public void HealthEndpoint_Always_CarriesNoRateLimitPolicy(string route)
    {
        // Arrange — ACA readiness probes poll these continuously (ADR-0025); a limiter here would
        // 429 the probe and pull the app out of the ingress rotation.
        EndpointDataSource endpointDataSource = Factory.Services.GetRequiredService<EndpointDataSource>();

        // Act
        RouteEndpoint endpoint = endpointDataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Single(routeEndpoint => routeEndpoint.RoutePattern.RawText?.TrimStart('/') == route);

        // Assert
        endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>().ShouldBeNull();
    }

    [Fact]
    public async Task ConnectToken_BurstWithSeededClient_RejectsRequestBeyondPermitLimit()
    {
        // Arrange — the seeded public client with a wrong password: admitted requests travel the
        // full OpenIddict validation + passthrough endpoint path before failing with 400.
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();

        HttpStatusCode[] statusCodes = new HttpStatusCode[AuthEndpointPermitLimit + 1];

        // Act
        for (int i = 0; i < statusCodes.Length; i++)
        {
            using FormUrlEncodedContent tokenRequest = CreateTokenRequest(clientId: "lotrokoniecdev-test");
            using HttpResponseMessage response = await client.PostAsync(
                new Uri("connect/token", UriKind.Relative), tokenRequest);
            statusCodes[i] = response.StatusCode;
        }

        // Assert — the first requests pass through to OpenIddict (fresh partition), the one past
        // the permit limit is cut off by the limiter.
        statusCodes.Take(AuthEndpointPermitLimit).ShouldAllBe(statusCode => statusCode != HttpStatusCode.TooManyRequests);
        statusCodes[AuthEndpointPermitLimit].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task ConnectToken_BurstWithUnknownClient_RejectsRequestBeyondPermitLimit()
    {
        // Arrange — an unknown client_id is the cheapest externally craftable junk: OpenIddict
        // rejects it during the authentication stage, so this burst proves the limiter counts
        // requests BEFORE OpenIddict sees them — the exact ordering a post-authentication limiter
        // silently loses.
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();

        HttpStatusCode[] statusCodes = new HttpStatusCode[AuthEndpointPermitLimit + 1];

        // Act
        for (int i = 0; i < statusCodes.Length; i++)
        {
            using FormUrlEncodedContent tokenRequest = CreateTokenRequest(clientId: "unknown-brute-force-client");
            using HttpResponseMessage response = await client.PostAsync(
                new Uri("connect/token", UriKind.Relative), tokenRequest);
            statusCodes[i] = response.StatusCode;
        }

        // Assert
        statusCodes.Take(AuthEndpointPermitLimit).ShouldAllBe(statusCode => statusCode != HttpStatusCode.TooManyRequests);
        statusCodes[AuthEndpointPermitLimit].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task ConnectIntrospect_BurstGuessingTheConfidentialClientSecret_RejectsRequestBeyondPermitLimit()
    {
        // Arrange — introspection has no routed handler of its own (OpenIddict middleware serves it
        // during the authentication stage), so this burst proves the metadata-only route genuinely
        // arms the limiter there: without it, the confidential api client's secret could be guessed
        // without any throttle (#349). Admitted requests fail client authentication inside
        // OpenIddict; the one past the permit limit never reaches it.
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();

        HttpStatusCode[] statusCodes = new HttpStatusCode[AuthEndpointPermitLimit + 1];

        // Act
        for (int i = 0; i < statusCodes.Length; i++)
        {
            using FormUrlEncodedContent introspectionRequest = CreateIntrospectionRequest();
            using HttpResponseMessage response = await client.PostAsync(
                new Uri("connect/introspect", UriKind.Relative), introspectionRequest);
            statusCodes[i] = response.StatusCode;
        }

        // Assert
        statusCodes.Take(AuthEndpointPermitLimit).ShouldAllBe(statusCode => statusCode != HttpStatusCode.TooManyRequests);
        statusCodes[AuthEndpointPermitLimit].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    private WebApplicationFactory<Program> CreateRateLimitedHost()
    {
        return Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "RateLimiting:ForceEnable", "true" }
                });
            });
        });
    }

    private static FormUrlEncodedContent CreateTokenRequest(string clientId)
    {
        return new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = "burst@lotro-translator.pl",
            ["password"] = "DefinitelyWrong1!",
            ["client_id"] = clientId
        });
    }

    private static FormUrlEncodedContent CreateIntrospectionRequest()
    {
        return new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = "not-a-real-reference-token",
            ["client_id"] = "lotrokoniecdev-api",
            ["client_secret"] = "DefinitelyWrongSecret1!"
        });
    }
}
