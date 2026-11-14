using System.Runtime.ExceptionServices;

namespace LotroKoniecDev.Frontend.Infrastructure.Auth;

/// <summary>
/// The OIDC handler loads the discovery document the first time it builds a sign-in redirect. With no copy
/// and the sign-in server down, that load throws inside the challenge and the visitor gets the error page
/// (#1153). Loading it here first, through the handler's own configuration manager, lets the login route
/// show "login temporarily unavailable" instead. On success the challenge reuses the copy.
/// </summary>
internal sealed partial class SignInAvailability
{
    private readonly SignInServerDiscovery _signInServerDiscovery;
    private readonly ILogger<SignInAvailability> _logger;

    /// <summary>
    /// The handler's post-configure always sets the configuration manager. A missing one still counts as
    /// available, so it can never turn a working sign-in into a false "unavailable".
    /// </summary>
    public async Task<bool> CanRedirectToSignInAsync(CancellationToken cancellationToken)
    {
        SignInServerDiscoveryResult discovery = await _signInServerDiscovery.ReadAsync(cancellationToken);
        if (discovery.Failure is not { } failure)
        {
            return true;
        }

        // A browser that left is not an outage: its abort goes up as a cancelled request.
        if (failure is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            ExceptionDispatchInfo.Throw(failure);
        }

        LogSignInServerUnreachable(_logger, failure);
        return false;
    }

    public SignInAvailability(
        SignInServerDiscovery signInServerDiscovery,
        ILogger<SignInAvailability> logger)
    {
        _signInServerDiscovery = signInServerDiscovery;
        _logger = logger;
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "The sign-in server's discovery document could not be read; the visitor gets the login-unavailable page.")]
    private static partial void LogSignInServerUnreachable(ILogger logger, Exception exception);
}
