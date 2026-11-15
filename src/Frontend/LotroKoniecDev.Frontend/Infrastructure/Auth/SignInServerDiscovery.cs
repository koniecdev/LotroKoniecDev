using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace LotroKoniecDev.Frontend.Infrastructure.Auth;

/// <summary>
/// Reads the sign-in server's discovery document through the OIDC handler's own configuration manager, so
/// the website calls only the addresses the server publishes and never builds one itself (#1114). It shares
/// the handler's copy. It never logs: each caller says what the failure stopped.
/// </summary>
internal sealed class SignInServerDiscovery
{
    private readonly IOptionsMonitor<OpenIdConnectOptions> _openIdConnectOptionsMonitor;

    /// <summary>
    /// With no copy yet, the manager fetches the document without the token, so <c>WaitAsync</c> is what ends
    /// the wait. Options that cannot be built are a bug, not an outage, so that exception is not caught here.
    /// </summary>
    public async Task<SignInServerDiscoveryResult> ReadAsync(CancellationToken cancellationToken)
    {
        IConfigurationManager<OpenIdConnectConfiguration>? configurationManager = _openIdConnectOptionsMonitor
            .Get(OpenIdConnectDefaults.AuthenticationScheme)
            .ConfigurationManager;
        if (configurationManager is null)
        {
            return SignInServerDiscoveryResult.NoConfigurationManager;
        }

        try
        {
            OpenIdConnectConfiguration configuration = await configurationManager
                .GetConfigurationAsync(cancellationToken)
                .WaitAsync(cancellationToken);
            return SignInServerDiscoveryResult.Read(configuration);
        }
        catch (Exception exception)
        {
            return SignInServerDiscoveryResult.Failed(exception);
        }
    }

    /// <summary>
    /// On Linux and macOS a bare path also parses as an absolute file:// address, so the scheme is checked
    /// too.
    /// </summary>
    public static Uri? AsWebAddress(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri
            : null;

    public SignInServerDiscovery(IOptionsMonitor<OpenIdConnectOptions> openIdConnectOptionsMonitor)
    {
        _openIdConnectOptionsMonitor = openIdConnectOptionsMonitor;
    }
}
