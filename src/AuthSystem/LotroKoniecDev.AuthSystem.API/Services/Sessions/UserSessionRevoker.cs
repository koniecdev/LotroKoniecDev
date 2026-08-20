using OpenIddict.Abstractions;

namespace LotroKoniecDev.AuthSystem.API.Services.Sessions;

/// <summary>
/// The <see cref="IUserSessionRevoker"/> built on the OpenIddict token and authorization managers. It
/// does the same revocation the DeleteAccount and Logout flows already do, so every flow that changes
/// credentials ends sessions through one shared implementation.
/// </summary>
internal sealed partial class UserSessionRevoker : IUserSessionRevoker
{
    private readonly IOpenIddictTokenManager _tokenManager;
    private readonly IOpenIddictAuthorizationManager _authorizationManager;
    private readonly ILogger<UserSessionRevoker> _logger;

    public UserSessionRevoker(
        IOpenIddictTokenManager tokenManager,
        IOpenIddictAuthorizationManager authorizationManager,
        ILogger<UserSessionRevoker> logger)
    {
        _tokenManager = tokenManager;
        _authorizationManager = authorizationManager;
        _logger = logger;
    }

    public async Task RevokeAllAsync(string userId, CancellationToken cancellationToken = default)
    {
        // Best effort. The credential change and the security stamp change have already succeeded, so a
        // temporary failure here must not fail the whole operation. It is logged, and the tokens that
        // survive expire soon anyway. DeleteAccount's cleanup works the same way.
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
