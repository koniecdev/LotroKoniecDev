using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients;

/// <summary>
/// Sends the visitor's address to an API next to the environment's key (ADR-0054, #823). Every call
/// leaves from this one container, so without it the API would meter all visitors as one client. The
/// address is the one <c>UseForwardedHeaders</c> resolved from Caddy, so a visitor cannot choose it. The
/// handler sits on every path to the auth API (the typed account client, the token client and the OIDC
/// handler's back-channel) and on the TMS API's typed client, each with the key of the API it calls. A
/// call made outside a request has no visitor and sends neither header, and so does a client with no
/// key, whose API ignores the address anyway.
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
