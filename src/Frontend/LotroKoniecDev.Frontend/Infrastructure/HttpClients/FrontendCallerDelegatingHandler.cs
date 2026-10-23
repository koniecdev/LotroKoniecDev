using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients;

/// <summary>
/// Sends the visitor's address next to the environment's key, so the API does not meter all visitors as
/// this one container (ADR-0054). The address is the one <c>UseForwardedHeaders</c> resolved from Caddy,
/// so a visitor cannot choose it. A call outside a request, or a client with no key, sends neither header.
/// </summary>
internal sealed class FrontendCallerDelegatingHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly string? _callerKey;

    public FrontendCallerDelegatingHandler(IHttpContextAccessor httpContextAccessor, string? callerKey)
    {
        _httpContextAccessor = httpContextAccessor;
        _callerKey = callerKey;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_callerKey)
            && _httpContextAccessor.HttpContext?.Connection.RemoteIpAddress is { } visitorAddress)
        {
            request.Headers.Add(FrontendCallerHeaders.Key, _callerKey);
            request.Headers.Add(FrontendCallerHeaders.ClientAddress, visitorAddress.ToString());
        }

        return base.SendAsync(request, cancellationToken);
    }
}
