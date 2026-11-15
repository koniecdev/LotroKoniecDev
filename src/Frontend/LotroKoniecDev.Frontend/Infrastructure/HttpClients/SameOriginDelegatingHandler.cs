namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients;

/// <summary>
/// Refuses any origin but the configured API before the bearer token and the caller key are added (#830):
/// an absolute link replaces the client's base address. It stays outside the resilience handler, so a
/// refusal is never retried and never opens the circuit breaker.
/// </summary>
internal sealed partial class SameOriginDelegatingHandler : DelegatingHandler
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
        if (request.RequestUri is { IsAbsoluteUri: true } requestUri && UriOrigin.AreSame(requestUri, _origin))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        string refusedOrigin = request.RequestUri is { IsAbsoluteUri: true } absoluteUri
            ? OriginOf(absoluteUri)
            : "(not an absolute address)";
        string configuredOrigin = OriginOf(_origin);
        LogRefused(_logger, refusedOrigin, configuredOrigin);

        throw new HttpRequestException(
            $"The request to '{refusedOrigin}' was refused, because it is not the configured origin '{configuredOrigin}'.");
    }

    /// <summary>
    /// <see cref="Uri.Authority"/> leaves out any <c>user:password@</c> part, so a link that carries one
    /// never reaches the log.
    /// </summary>
    private static string OriginOf(Uri uri) => $"{uri.Scheme}://{uri.Authority}";

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Refused an API request to {RefusedOrigin}: the client may only call {ConfiguredOrigin}.")]
    private static partial void LogRefused(ILogger logger, string refusedOrigin, string configuredOrigin);
}
