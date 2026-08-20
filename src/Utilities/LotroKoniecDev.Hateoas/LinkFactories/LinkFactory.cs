using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.Hateoas.LinkFactories;

/// <summary>
/// Turns a named endpoint into an absolute URI with ASP.NET's <see cref="LinkGenerator"/>, and emits
/// the link only when the caller could really follow it. It returns <see langword="null"/> when the
/// endpoint cannot be found (a typo in the name, or a missing <c>WithName(...)</c> on the route) or
/// when the endpoint's authorization would answer 401 or 403. Callers collect the links that are not
/// null with <see cref="LinkListExtensions.AddIfPresent"/>.
/// <para>
/// The check reads the target endpoint's own metadata instead of repeating role rules in each link
/// factory. The endpoint's policy stays the one source of truth, so a link can never promise
/// something the endpoint would refuse.
/// </para>
/// </summary>
internal sealed partial class LinkFactory : ILinkFactory
{
    private readonly LinkGenerator _linkGenerator;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IEndpointAddressScheme<string> _endpointAddressScheme;
    private readonly IAuthorizationPolicyProvider _authorizationPolicyProvider;
    private readonly IAuthorizationService _authorizationService;
    private readonly ILogger<LinkFactory> _logger;

    public LinkFactory(
        LinkGenerator linkGenerator,
        IHttpContextAccessor httpContextAccessor,
        IEndpointAddressScheme<string> endpointAddressScheme,
        IAuthorizationPolicyProvider authorizationPolicyProvider,
        IAuthorizationService authorizationService,
        ILogger<LinkFactory> logger)
    {
        _linkGenerator = linkGenerator;
        _httpContextAccessor = httpContextAccessor;
        _endpointAddressScheme = endpointAddressScheme;
        _authorizationPolicyProvider = authorizationPolicyProvider;
        _authorizationService = authorizationService;
        _logger = logger;
    }

    public async ValueTask<LinkDto?> CreateAsync(string endpoint, string rel, string method, object? values = null)
    {
        HttpContext? httpContext = _httpContextAccessor.HttpContext;
        ArgumentNullException.ThrowIfNull(httpContext);

        string? href = _linkGenerator.GetUriByName(httpContext, endpoint, values);

        if (href is null)
        {
            LogHateoasLinksGenerationFailure(_logger, endpoint, rel, method, values);

            return null;
        }

        if (!await IsCallerAuthorizedAsync(httpContext, endpoint))
        {
            return null;
        }

        return new LinkDto(Href: href, Rel: rel, Method: method);
    }

    /// <summary>
    /// Runs ASP.NET's own authorization check for the target endpoint against the current caller.
    /// It fails closed: an endpoint we cannot find is never advertised.
    /// </summary>
    private async ValueTask<bool> IsCallerAuthorizedAsync(HttpContext httpContext, string endpointName)
    {
        // This is the address scheme LinkGenerator.GetUriByName just used, so the lookup lands on the
        // very endpoint whose URI was built, not on a second one that happens to match.
        Endpoint? endpoint = _endpointAddressScheme.FindEndpoints(endpointName).FirstOrDefault();

        if (endpoint is null)
        {
            return false;
        }

        // Same order as AuthorizationMiddleware: AllowAnonymous beats every policy, the fallback one
        // included. It has to come before CombineAsync, because AllowAnonymousAttribute is not
        // IAuthorizeData: in an authorized-by-default app an anonymous endpoint still combines into the
        // fallback policy. Check the policy first and every public link disappears for guests.
        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            return true;
        }

        // When the endpoint has no authorize metadata of its own, CombineAsync adds the application's
        // fallback policy. An authorized-by-default API is then judged exactly as the middleware
        // would judge it.
        AuthorizationPolicy? policy = await AuthorizationPolicy.CombineAsync(
            _authorizationPolicyProvider,
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>());

        if (policy is null)
        {
            return true;
        }

        // No policy in either API names an authentication scheme, so the user ASP.NET already
        // authenticated for this request is the one the target endpoint would see. Only the policy's
        // requirements are left to check.
        AuthorizationResult authorizationResult = await _authorizationService.AuthorizeAsync(
            httpContext.User,
            resource: null,
            policy.Requirements);

        return authorizationResult.Succeeded;
    }

    [LoggerMessage(
        EventId = EventIds.LinksGenerationFailure,
        Level = LogLevel.Warning,
        Message =
            "HATEOAS link could not be generated. Endpoint={Endpoint}, Rel={Rel}, Method={Method}, Values={@Values}")]
    public static partial void LogHateoasLinksGenerationFailure(
        ILogger logger,
        string endpoint,
        string rel,
        string method,
        object? values);
}
