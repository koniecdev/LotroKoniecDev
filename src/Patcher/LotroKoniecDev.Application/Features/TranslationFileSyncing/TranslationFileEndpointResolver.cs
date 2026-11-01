using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Domain.Core.Errors;
using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.Application.Features.TranslationFileSyncing;

/// <summary>
/// Turns the one configured input, the TMS root URL, into the absolute URI the translation file is
/// downloaded from, by the <c>translation-file</c> link relation in the service document.
/// <para>
/// Discovery comes first (#611). A stored href that worked before only covers an outage, and nothing
/// here builds a path of its own. A server that answers but does <i>not</i> offer the rel gets no
/// fallback: a missing rel means the endpoint is not on offer, not that the server is down.
/// </para>
/// </summary>
internal sealed partial class TranslationFileEndpointResolver : ITranslationFileEndpointResolver
{
    /// <summary>
    /// Rel names are a frozen public contract (ADR-0041). This is the one string the CLI may know,
    /// because renaming it is the one change the server may never make.
    /// </summary>
    public const string TranslationFileRel = "translation-file";

    private const string GetMethod = "GET";

    private readonly ITranslationSystemDiscoveryClient _discoveryClient;
    private readonly ILogger<TranslationFileEndpointResolver> _logger;

    public TranslationFileEndpointResolver(
        ITranslationSystemDiscoveryClient discoveryClient,
        ILogger<TranslationFileEndpointResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(discoveryClient);
        ArgumentNullException.ThrowIfNull(logger);

        _discoveryClient = discoveryClient;
        _logger = logger;
    }

    public async Task<Result<Uri>> ResolveAsync(string baseUrl, string? cachedHref, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? baseUri))
        {
            return Result.Failure<Uri>(DomainErrors.TranslationFileSync.EndpointRejected(
                baseUrl, "the configured TMS base URL is not an absolute URI."));
        }

        Result<IReadOnlyList<DiscoveredLink>> discovery =
            await _discoveryClient.FetchLinksAsync(baseUrl, cancellationToken);

        if (discovery.IsFailure)
        {
            return UseCachedHref(baseUri, cachedHref, discovery.Error);
        }

        DiscoveredLink? link = discovery.Value.FirstOrDefault(candidate =>
            string.Equals(candidate.Rel, TranslationFileRel, StringComparison.Ordinal)
            && string.Equals(candidate.Method, GetMethod, StringComparison.OrdinalIgnoreCase));

        if (link is null)
        {
            return Result.Failure<Uri>(DomainErrors.TranslationFileSync.EndpointNotAdvertised(TranslationFileRel));
        }

        return ValidateHref(baseUri, link.Href);
    }

    private Result<Uri> UseCachedHref(Uri baseUri, string? cachedHref, Error discoveryError)
    {
        if (string.IsNullOrWhiteSpace(cachedHref))
        {
            return Result.Failure<Uri>(DomainErrors.TranslationFileSync.EndpointDiscoveryUnavailable(
                $"{discoveryError.Message} No endpoint was cached from an earlier run, and a path is never guessed."));
        }

        Result<Uri> cached = ValidateHref(baseUri, cachedHref);
        if (cached.IsFailure)
        {
            // The cached href is data from disk, so it is checked like one just discovered (#397). It
            // also fails here when --tms-url now points at another host: the cached endpoint belongs to
            // the old one and must not be reused.
            return Result.Failure<Uri>(DomainErrors.TranslationFileSync.EndpointDiscoveryUnavailable(
                $"{discoveryError.Message} The cached endpoint could not be reused: {cached.Error.Message}"));
        }

        LogFallbackToCachedEndpoint(_logger, discoveryError.Message, cached.Value.ToString());
        return cached;
    }

    private static Result<Uri> ValidateHref(Uri baseUri, string href)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out Uri? uri))
        {
            return Result.Failure<Uri>(DomainErrors.TranslationFileSync.EndpointRejected(
                href, "it is not an absolute URI."));
        }

        // The same rule as for the configured base URL: over plain http anyone on the path can change
        // the file (#391), so only loopback, with no network hop, may skip TLS.
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        {
            return Result.Failure<Uri>(DomainErrors.TranslationFileSync.EndpointRejected(
                href, "only https is allowed (plain http only for localhost)."));
        }

        // Whoever controls the base URL controls this href too, so the document may change the path
        // but never the origin. A link pointing somewhere else is a redirect to any host at all, not
        // an entry point.
        if (!string.Equals(uri.Scheme, baseUri.Scheme, StringComparison.Ordinal)
            || !string.Equals(uri.Authority, baseUri.Authority, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure<Uri>(DomainErrors.TranslationFileSync.EndpointRejected(
                href, $"it points at a different origin than the configured TMS base URL '{baseUri.Scheme}://{baseUri.Authority}'."));
        }

        return Result.Success(uri);
    }

    [LoggerMessage(
        EventId = EventIds.TranslationFileEndpointFallbackToCache,
        Level = LogLevel.Warning,
        Message = "TMS discovery unavailable ({Error}); falling back to the cached translation-file endpoint {Endpoint}")]
    private static partial void LogFallbackToCachedEndpoint(ILogger logger, string error, string endpoint);
}
