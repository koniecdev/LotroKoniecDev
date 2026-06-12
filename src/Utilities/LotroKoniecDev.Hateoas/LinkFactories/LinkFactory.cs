using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.Hateoas.LinkFactories;

/// <summary>
/// Resolves named endpoints to absolute URIs via ASP.NET's
/// <see cref="LinkGenerator"/>. Returns <see langword="null"/> when the
/// endpoint cannot be resolved (e.g. typo in the endpoint name or missing
/// <c>WithName(...)</c> on the route); callers collect non-null links via
/// <see cref="LinkListExtensions.AddIfPresent"/>.
/// </summary>
internal sealed partial class LinkFactory : ILinkFactory
{
    private readonly LinkGenerator _linkGenerator;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<LinkFactory> _logger;

    public LinkFactory(
        LinkGenerator linkGenerator,
        IHttpContextAccessor httpContextAccessor,
        ILogger<LinkFactory> logger)
    {
        _linkGenerator = linkGenerator;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public LinkDto? Create(string endpoint, string rel, string method, object? values = null)
    {
        ArgumentNullException.ThrowIfNull(_httpContextAccessor.HttpContext);

        string? href = _linkGenerator.GetUriByName(
            _httpContextAccessor.HttpContext,
            endpoint,
            values
        );

        if (href is not null)
        {
            return new LinkDto(Href: href, Rel: rel, Method: method);
        }
        
        LogHateoasLinksGenerationFailure(_logger, endpoint, rel, method, values);
        
        return null;
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
