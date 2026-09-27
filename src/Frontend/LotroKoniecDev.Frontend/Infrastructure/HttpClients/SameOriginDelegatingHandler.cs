namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients;

/// <summary>
/// Refuses a request that leaves the origin of the API this client is configured for (#830). The
/// frontend follows the links an API sends, and an absolute link replaces the client's base address.
/// The handlers after this one add the translator's bearer token and the box's caller key to whatever
/// address they get, so a link to another host would carry both there. It sits first in the pipeline,
/// so a refused request is never retried and never counts toward the circuit breaker. It surfaces as a
/// transport failure. The client's socket handler does not follow redirects, because a redirect to
/// another host would carry the caller key past this check. The CLI works the same way (#611). A
/// split-off service (ADR-0041) gets its own typed client with its own base address.
/// </summary>
internal sealed class SameOriginDelegatingHandler : DelegatingHandler
{
    private readonly Uri _origin;
    private readonly ILogger<SameOriginDelegatingHandler> _logger;

    public SameOriginDelegatingHandler(Uri origin, ILogger<SameOriginDelegatingHandler> logger)
    {
        _origin = origin;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri is { IsAbsoluteUri: true } requestUri && IsSameOrigin(requestUri))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        string refusedOrigin = request.RequestUri is { IsAbsoluteUri: true } absoluteUri
            ? OriginOf(absoluteUri)
            : "(not an absolute address)";
        string configuredOrigin = OriginOf(_origin);
        LogRefused(_logger, refusedOrigin, configuredOrigin, null);

        throw new HttpRequestException(
            $"The request to '{refusedOrigin}' was refused, because it is not the configured origin '{configuredOrigin}'.");
    }

    private bool IsSameOrigin(Uri requestUri) =>
        string.Equals(requestUri.Scheme, _origin.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(requestUri.IdnHost, _origin.IdnHost, StringComparison.OrdinalIgnoreCase)
        && requestUri.Port == _origin.Port;

    /// <summary>
    /// <see cref="Uri.Authority"/> leaves out any <c>user:password@</c> part, so a link that carries one
    /// never reaches the log.
    /// </summary>
    private static string OriginOf(Uri uri) => $"{uri.Scheme}://{uri.Authority}";

    private static readonly Action<ILogger, string, string, Exception?> LogRefused =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(1, nameof(LogRefused)),
            "Refused an API request to {RefusedOrigin}: the client may only call {ConfiguredOrigin}.");
}
