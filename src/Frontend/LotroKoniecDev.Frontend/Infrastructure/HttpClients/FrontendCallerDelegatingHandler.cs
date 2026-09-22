using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.Contracts.Common;
using LotroKoniecDev.Frontend.Settings;

namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients;

/// <summary>
/// Sends the visitor's address to the auth API next to the environment's key (ADR-0054). Every call
/// leaves from this one container, so without it the auth API would meter all visitors as one client.
/// The address is the one <c>UseForwardedHeaders</c> resolved from Caddy, so a visitor cannot choose
/// it. The handler sits on every path to the auth API: the typed account client, the token client and
/// the OIDC handler's back-channel. A call made outside a request has no visitor and sends neither
/// header, and so does a host with no key, whose auth API ignores the address anyway.
/// </summary>
internal sealed class FrontendCallerDelegatingHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly string? _callerKey;

    public FrontendCallerDelegatingHandler(
        IHttpContextAccessor httpContextAccessor,
        IOptions<AuthSystemSettings> authSystemSettings)
    {
        _httpContextAccessor = httpContextAccessor;
        _callerKey = authSystemSettings.Value.CallerKey;
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
