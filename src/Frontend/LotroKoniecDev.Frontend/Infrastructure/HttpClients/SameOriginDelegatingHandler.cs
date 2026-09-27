namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients;

/// <summary>
/// Refuses a request to any origin other than the API this client is configured for, before the bearer
/// token and the caller key are added (#830, ADR-0041). An absolute link from an API replaces the
/// client's base address, so without it a link to another host would carry both there. It must stay
/// outside the resilience handler, so a refusal is never retried and never opens the circuit breaker.
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
