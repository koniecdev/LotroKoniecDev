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
    /// The same as one Npgsql command timeout. The revoke is two bulk updates that finish far sooner,
    /// so this only stops a stuck database from holding the request and its connection forever.
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
            // One bulk update each, so the cost does not grow with the rows a busy account builds up.
            // Authorizations go first: OpenIddict refuses a refresh token whose authorization is revoked,
            // so a refresh that lands between the two updates still gets a dead token.
            long revokedAuthorizations = await _authorizationManager.RevokeBySubjectAsync(userId, cancellationToken);
            long revokedTokens = await _tokenManager.RevokeBySubjectAsync(userId, cancellationToken);

            LogSessionsRevoked(_logger, userId, revokedTokens, revokedAuthorizations);
        }
        catch (Exception ex)
        {
            LogRevocationFailed(_logger, ex, userId);
        }
    }

    [LoggerMessage(EventId = EventIds.UserSessionsRevoked, Level = LogLevel.Information, Message = "Revoked all sessions for user {UserId}: {TokenCount} token(s), {AuthorizationCount} authorization(s)")]
    private static partial void LogSessionsRevoked(ILogger logger, string userId, long tokenCount, long authorizationCount);

    [LoggerMessage(EventId = EventIds.UserSessionsRevocationFailed, Level = LogLevel.Error, Message = "Failed to revoke sessions for user {UserId}. Refresh tokens that were not revoked stay usable until they expire.")]
    private static partial void LogRevocationFailed(ILogger logger, Exception exception, string userId);
}
