using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace LotroKoniecDev.Frontend.Infrastructure.Auth;

/// <summary>
/// Holds the document, the reason it could not be read, or neither when the handler has no configuration
/// manager.
/// </summary>
internal sealed class SignInServerDiscoveryResult
{
    /// <summary>The handler's post-configure always sets one, so only a test or a broken setup gets this.</summary>
    public static readonly SignInServerDiscoveryResult NoConfigurationManager = new(null, null);

    public OpenIdConnectConfiguration? Configuration { get; }

    /// <summary>The sign-in server is down or slow, the time limit passed, or the caller left.</summary>
    public Exception? Failure { get; }

    public static SignInServerDiscoveryResult Read(OpenIdConnectConfiguration configuration) => new(configuration, null);

    public static SignInServerDiscoveryResult Failed(Exception failure) => new(null, failure);

    private SignInServerDiscoveryResult(OpenIdConnectConfiguration? configuration, Exception? failure)
    {
        Configuration = configuration;
        Failure = failure;
    }
}
