using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace LotroKoniecDev.Frontend.Infrastructure.Auth;

/// <summary>
/// The OIDC handler builds the sign-in redirect from the sign-in server's discovery document, and it loads
/// that document the first time it needs it. When the website holds no copy yet and the sign-in server does
/// not answer, the load throws inside the challenge, and the visitor gets the error page (#311, #1153).
/// Loading the document here first, through the same configuration manager the handler uses, lets the login
/// route, and a failed return from the sign-in, show the "login temporarily unavailable" page instead. When
/// the load works, the challenge reuses the copy and redirects exactly as before.
/// </summary>
internal sealed partial class SignInAvailability
{
    private readonly IOptionsMonitor<OpenIdConnectOptions> _openIdConnectOptionsMonitor;
    private readonly ILogger<SignInAvailability> _logger;

    /// <summary>
    /// Once the OIDC handler post-configures its options, the configuration manager is always set. A missing
    /// one still counts as available, like in <see cref="SignOut.RefreshTokenRevoker"/>, so it can never turn
    /// a working sign-in into a false "unavailable".
    /// </summary>
    public async Task<bool> CanRedirectToSignInAsync(CancellationToken cancellationToken)
    {
        IConfigurationManager<OpenIdConnectConfiguration>? configurationManager = _openIdConnectOptionsMonitor
            .Get(OpenIdConnectDefaults.AuthenticationScheme)
            .ConfigurationManager;
        if (configurationManager is null)
        {
            return true;
        }

        // A browser that left while it waited for the document is not an outage. Its abort goes up as a
        // cancelled request, which the error handler drops without a warning. The manager runs its fetch
        // without the token, so WaitAsync is what stops the wait, and a fetch that fails after the browser
        // left is still an outage.
        try
        {
            await configurationManager
                .GetConfigurationAsync(cancellationToken)
                .WaitAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          || !cancellationToken.IsCancellationRequested)
        {
            LogSignInServerUnreachable(_logger, exception);
            return false;
        }
    }

    public SignInAvailability(
        IOptionsMonitor<OpenIdConnectOptions> openIdConnectOptionsMonitor,
        ILogger<SignInAvailability> logger)
    {
        _openIdConnectOptionsMonitor = openIdConnectOptionsMonitor;
        _logger = logger;
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "The sign-in server's discovery document could not be read; the visitor gets the login-unavailable page.")]
    private static partial void LogSignInServerUnreachable(ILogger logger, Exception exception);
}
