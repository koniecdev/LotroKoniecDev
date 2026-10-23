using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.Hateoas.LinkFactories;

/// <summary>
/// The check replays the target endpoint's own authorization metadata instead of repeating role rules
/// in each link factory, so a link never promises something the endpoint would refuse (ADR-0040).
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
    /// Fails closed: an endpoint we cannot find is never advertised.
    /// </summary>
    private async ValueTask<bool> IsCallerAuthorizedAsync(HttpContext httpContext, string endpointName)
    {
        // The address scheme LinkGenerator.GetUriByName just used, so the lookup lands on the endpoint
        // whose URI was built, not on a second one that happens to match.
        Endpoint? endpoint = _endpointAddressScheme.FindEndpoints(endpointName).FirstOrDefault();

        if (endpoint is null)
        {
            return false;
        }

        // Same order as AuthorizationMiddleware: AllowAnonymous beats every policy, the fallback one
        // included. An anonymous endpoint still combines into the fallback policy, so checking the
        // policy first would hide every public link from guests.
        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            return true;
        }

        // With no authorize metadata of its own, CombineAsync adds the fallback policy, so an
        // authorized-by-default API is judged as the middleware would judge it.
        AuthorizationPolicy? policy = await AuthorizationPolicy.CombineAsync(
            _authorizationPolicyProvider,
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>());

        if (policy is null)
        {
            return true;
        }

        // No policy in either API names an authentication scheme, so the user already authenticated
        // for this request is the one the target endpoint would see. Only the requirements are left.
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
