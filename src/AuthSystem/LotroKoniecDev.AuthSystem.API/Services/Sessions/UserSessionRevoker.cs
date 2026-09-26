using OpenIddict.Abstractions;

namespace LotroKoniecDev.AuthSystem.API.Services.Sessions;

/// <summary>
/// The <see cref="IUserSessionRevoker"/> built on the OpenIddict token and authorization managers. Every
/// flow that changes credentials or schedules a deletion ends sessions through this one class, so each
/// of them gets the same guarantee about the request's cancel signal (#872).
/// </summary>
internal sealed partial class UserSessionRevoker : IUserSessionRevoker
{
    /// <summary>
    /// Long enough for an account with a few hundred token rows, short enough that a stuck database
    /// cannot hold the request and its connection forever.
    /// </summary>
    internal static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(30);

    private readonly IOpenIddictTokenManager _tokenManager;
    private readonly IOpenIddictAuthorizationManager _authorizationManager;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<UserSessionRevoker> _logger;

    public UserSessionRevoker(
        IOpenIddictTokenManager tokenManager,
        IOpenIddictAuthorizationManager authorizationManager,
        TimeProvider timeProvider,
        ILogger<UserSessionRevoker> logger)
    {
        _tokenManager = tokenManager;
        _authorizationManager = authorizationManager;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task RevokeAllAsync(string userId)
    {
        // Best effort: the change is already saved, so a failure here must not fail it, and it is only
        // logged. A refresh token that survives keeps working, though, because the refresh grant does not
        // check the security stamp. So nothing may stop this early on purpose: it ignores the request's
        // cancel signal, and a browser that drops the request now cannot keep the other devices signed
        // in (#872). The time limit only stops a stuck database from holding the request forever.
        using CancellationTokenSource timeLimit = new(TimeLimit, _timeProvider);
        CancellationToken cancellationToken = timeLimit.Token;

        try
        {
            int revokedTokens = 0;
            await foreach (object token in _tokenManager.FindBySubjectAsync(userId, cancellationToken))
            {
                await _tokenManager.TryRevokeAsync(token, cancellationToken);
                revokedTokens++;
            }

            int revokedAuthorizations = 0;
            await foreach (object authorization in _authorizationManager.FindBySubjectAsync(userId, cancellationToken))
            {
                await _authorizationManager.TryRevokeAsync(authorization, cancellationToken);
                revokedAuthorizations++;
            }

            LogSessionsRevoked(_logger, userId, revokedTokens, revokedAuthorizations);
        }
        catch (Exception ex)
        {
            LogRevocationFailed(_logger, ex, userId);
        }
    }

    [LoggerMessage(EventId = EventIds.UserSessionsRevoked, Level = LogLevel.Information, Message = "Revoked all sessions for user {UserId}: {TokenCount} token(s), {AuthorizationCount} authorization(s)")]
    private static partial void LogSessionsRevoked(ILogger logger, string userId, int tokenCount, int authorizationCount);

    [LoggerMessage(EventId = EventIds.UserSessionsRevocationFailed, Level = LogLevel.Error, Message = "Failed to revoke sessions for user {UserId}. Outstanding tokens will expire naturally.")]
    private static partial void LogRevocationFailed(ILogger logger, Exception exception, string userId);
}
