using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.Hateoas.LinkFactories;

/// <summary>
/// Resolves named endpoints to absolute URIs via ASP.NET's
/// <see cref="LinkGenerator"/>, then emits the link only when the current caller
/// would actually be allowed to follow it. Returns <see langword="null"/> when the
/// endpoint cannot be resolved (e.g. typo in the endpoint name or missing
/// <c>WithName(...)</c> on the route) or when the target endpoint's own
/// authorization would answer 401/403; callers collect non-null links via
/// <see cref="LinkListExtensions.AddIfPresent"/>.
/// <para>
/// The authorization decision reads the <em>target</em> endpoint's metadata rather
/// than re-stating role rules in each link factory, so an endpoint's policy stays
/// the single source of truth and a link can never drift from what following it
/// would really do.
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
    /// Replays ASP.NET's own authorization decision for the target endpoint against the caller of
    /// the current request. Fails closed: an endpoint that cannot be located advertises nothing.
    /// </summary>
    private async ValueTask<bool> IsCallerAuthorizedAsync(HttpContext httpContext, string endpointName)
    {
        // The same address scheme LinkGenerator.GetUriByName just used, so this is a dictionary
        // lookup on the very endpoint whose URI was generated — never a second, divergent match.
        Endpoint? endpoint = _endpointAddressScheme.FindEndpoints(endpointName).FirstOrDefault();

        if (endpoint is null)
        {
            return false;
        }

        // Mirrors AuthorizationMiddleware: AllowAnonymous beats every policy, the fallback included.
        // This check MUST come before CombineAsync: AllowAnonymousAttribute is not IAuthorizeData, so
        // an anonymous endpoint under an authorized-by-default app still combines to the fallback
        // policy. Evaluate that first and every public endpoint's link vanishes for guests.
        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            return true;
        }

        // CombineAsync folds in the application's fallback policy when the endpoint carries no
        // authorize metadata of its own, so an authorized-by-default API is evaluated exactly as
        // the middleware would evaluate it.
        AuthorizationPolicy? policy = await AuthorizationPolicy.CombineAsync(
            _authorizationPolicyProvider,
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>());

        if (policy is null)
        {
            return true;
        }

        // No policy in either API names an authentication scheme, so the principal ASP.NET already
        // authenticated for THIS request is the one the target endpoint would see; only the policy's
        // requirements are left to evaluate.
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
