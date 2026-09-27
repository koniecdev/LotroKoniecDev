namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients;

/// <summary>
/// Refuses a request that leaves the origin of the API this client is configured for (#830). The
/// frontend follows the links an API sends, and an absolute link replaces the client's base address.
/// The handlers after this one add the translator's bearer token and the box's caller key to whatever
/// address they get, so a link to another host would carry both there. It sits first in the pipeline:
/// a refused request reaches no header handler and no retry, and it surfaces as a transport failure.
/// The CLI refuses an off-origin link the same way (#611). A split-off service (ADR-0041) gets its own
/// typed client with its own base address, never a link through this one.
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
            ? absoluteUri.GetLeftPart(UriPartial.Authority)
            : "(not an absolute address)";
        string configuredOrigin = _origin.GetLeftPart(UriPartial.Authority);
        LogRefused(_logger, refusedOrigin, configuredOrigin, null);

        throw new HttpRequestException(
            $"The request to '{refusedOrigin}' was refused, because it is not the configured origin '{configuredOrigin}'.");
    }

    private bool IsSameOrigin(Uri requestUri) =>
        string.Equals(requestUri.Scheme, _origin.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(requestUri.IdnHost, _origin.IdnHost, StringComparison.OrdinalIgnoreCase)
        && requestUri.Port == _origin.Port;

    private static readonly Action<ILogger, string, string, Exception?> LogRefused =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(1, nameof(LogRefused)),
            "Refused an API request to {RefusedOrigin}: the client may only call {ConfiguredOrigin}.");
}
