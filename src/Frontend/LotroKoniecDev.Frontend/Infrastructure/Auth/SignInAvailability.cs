using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace LotroKoniecDev.Frontend.Infrastructure.Auth;

/// <summary>
/// The OIDC handler loads the discovery document the first time it builds a sign-in redirect. With no copy
/// and the sign-in server down, that load throws inside the challenge and the visitor gets the error page
/// (#1153). Loading it here first, through the handler's own configuration manager, lets the login route
/// show "login temporarily unavailable" instead. On success the challenge reuses the copy.
/// </summary>
internal sealed partial class SignInAvailability
{
    private readonly IOptionsMonitor<OpenIdConnectOptions> _openIdConnectOptionsMonitor;
    private readonly ILogger<SignInAvailability> _logger;

    /// <summary>
    /// The handler's post-configure always sets the configuration manager. A missing one still counts as
    /// available, so it can never turn a working sign-in into a false "unavailable".
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

        // A browser that left is not an outage: its abort goes up as a cancelled request. The manager
        // fetches without the token, so WaitAsync is what stops the wait.
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
