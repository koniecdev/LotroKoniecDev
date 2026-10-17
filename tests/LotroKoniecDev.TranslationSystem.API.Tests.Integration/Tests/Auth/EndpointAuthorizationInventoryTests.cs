using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using LotroKoniecDev.TranslationSystem.API.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// Generated inventories over every mapped endpoint (the AUDIT-TEST-02 class, made permanent).
/// The hand-listed 403 matrix in <see cref="AuthorizationDefaultsTests"/> decays as endpoints are
/// added; these sweeps enumerate <see cref="EndpointDataSource"/> instead, so a new endpoint joins
/// the gate the moment it is mapped — going public stays a reviewed, whitelisted decision.
/// </summary>
[Collection("TranslationApi")]
public sealed partial class EndpointAuthorizationInventoryTests
{
    /// <summary>
    /// The deliberate anonymous surface. An entry here is a reviewed decision with a reason —
    /// never add one just to turn this test green.
    /// </summary>
    private static readonly IReadOnlyList<string> DeliberatePublicSurface =
    [
        // Liveness/readiness probes — the orchestrator cannot authenticate.
        "ANY /health",
        "ANY /health/live",
        "ANY /health/ready",

        // The landing page's progress counters are public marketing data by design.
        "GET /api/v1/progress",

        // The CLI auto-download (M2-20) runs on players' machines without accounts.
        "GET /api/v1/translation-files/{lang}",

        // The translations list is publicly readable since #309 (read-only browsing).
        "GET /api/v1/translations",
    ];

    private readonly TranslationSystemApiFactory _factory;

    public EndpointAuthorizationInventoryTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void AnonymousEndpoints_AcrossTheWholeApp_MatchTheDeliberatePublicSurface()
    {
        // Act
        List<string> anonymousSurface = GetRouteEndpoints()
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .SelectMany(endpoint => MethodsOf(endpoint).Select(method => $"{method} {TemplateOf(endpoint)}"))
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToList();

        // Assert
        anonymousSurface.ShouldBe(
            DeliberatePublicSurface.OrderBy(entry => entry, StringComparer.Ordinal).ToList(),
            $"The anonymous surface changed. Actual: [{string.Join(", ", anonymousSurface)}]. " +
            "A newly public endpoint is a security decision — whitelist it here with a reason, " +
            "or remove its AllowAnonymous.");
    }

    [Fact]
    public async Task RoleGatedEndpoints_WithUnrecognizedRole_AllReturn403()
    {
        // Arrange — a correctly-signed token whose role is neither Admin nor Translator, sent to
        // every endpoint carrying a role policy. Authorization short-circuits before binding, so
        // write routes need no body; route parameters are satisfied with a dummy GUID.
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TranslationSystemApiFactory.CreateAccessToken("Reviewer"));

        List<(string Method, string Route)> gatedEndpoints = GetRouteEndpoints()
            .Where(HasRolePolicy)
            .SelectMany(endpoint => MethodsOf(endpoint)
                .Select(method => (Method: method, Route: RequestableRouteOf(endpoint))))
            .ToList();

        gatedEndpoints.ShouldNotBeEmpty("The sweep found no role-gated endpoints — it has gone vacuous.");

        // Act
        List<string> offenders = [];

        foreach ((string method, string route) in gatedEndpoints)
        {
            using HttpRequestMessage request = new(new HttpMethod(method), route);
            HttpResponseMessage response = await client.SendAsync(request);

            if (response.StatusCode != HttpStatusCode.Forbidden)
            {
                offenders.Add($"{method} {route} -> {(int)response.StatusCode}");
            }
        }

        // Assert
        offenders.ShouldBeEmpty(
            $"Role-gated endpoints that did not return 403 for an unrecognized role: " +
            $"[{string.Join(", ", offenders)}].");
    }

    private List<RouteEndpoint> GetRouteEndpoints()
    {
        return _factory.Services.GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .ToList();
    }

    private static bool HasRolePolicy(RouteEndpoint endpoint)
    {
        return endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(data => data.Policy is AuthorizationPolicies.RequireTranslatorRole
                or AuthorizationPolicies.RequireAdminRole);
    }

    private static IEnumerable<string> MethodsOf(RouteEndpoint endpoint)
    {
        return endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["ANY"];
    }

    private static string TemplateOf(RouteEndpoint endpoint)
    {
        return "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/');
    }

    private static string RequestableRouteOf(RouteEndpoint endpoint)
    {
        return RouteParameterRegex().Replace(TemplateOf(endpoint), "11111111-1111-1111-1111-111111111111");
    }

    [GeneratedRegex(@"\{[^}]+\}")]
    private static partial Regex RouteParameterRegex();
}
