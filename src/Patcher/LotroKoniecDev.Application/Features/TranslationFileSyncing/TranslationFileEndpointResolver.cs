using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Domain.Core.Errors;
using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.Application.Features.TranslationFileSyncing;

/// <summary>
/// Turns the one configured input — the TMS root URL — into the absolute URI the translation file is
/// downloaded from, by asking the service document for the <c>translation-file</c> link relation.
/// <para>
/// Discovery first, cache as the safety net (#611): a stored last-known-good href covers an outage,
/// but it is never the primary path, and nothing here composes a path of its own. A server that
/// answers and does <i>not</i> advertise the rel gets no fallback — an absent rel means the endpoint
/// is not on offer, which is a different statement from "the server is down".
/// </para>
/// </summary>
internal sealed partial class TranslationFileEndpointResolver : ITranslationFileEndpointResolver
{
    /// <summary>
    /// The link relation the TMS advertises its distribution endpoint under. Rel names are a frozen
    /// public contract (ADR-0041) — this is the one string the CLI is allowed to know, precisely
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
            // The sidecar is on-disk data, so it is validated exactly like a freshly discovered href
            // (AUDIT-SEC-07). It also fails here when the operator repointed --tms-url at another
            // host: the cached endpoint belongs to the old one and must not be reused.
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

        // Same rule the configured base URL passes through: plain http hands the file to any on-path
        // attacker (AUDIT-SEC-01), so only loopback — where there is no network hop — may skip TLS.
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        {
            return Result.Failure<Uri>(DomainErrors.TranslationFileSync.EndpointRejected(
                href, "only https is allowed (plain http only for localhost)."));
        }

        // The href is attacker-influenceable whenever the base URL is, so the document may move the
        // path but never the origin: a link pointing anywhere else is a redirect to an arbitrary
        // host, not an entry point.
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
