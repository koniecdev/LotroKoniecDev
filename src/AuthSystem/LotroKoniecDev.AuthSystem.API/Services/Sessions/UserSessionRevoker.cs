using OpenIddict.Abstractions;

namespace LotroKoniecDev.AuthSystem.API.Services.Sessions;

/// <summary>
/// Default <see cref="IUserSessionRevoker"/> backed by the OpenIddict token and authorization managers.
/// Mirrors the revocation the DeleteAccount and Logout flows already perform, so every credential-change
/// flow evicts sessions through one shared implementation.
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
        // Best-effort: the credential change and its security-stamp rotation have already succeeded, so a
        // transient revocation failure must not fail the whole operation — it is logged and the surviving
        // tokens expire at their (short) lifetimes. Same posture as DeleteAccount's artifact cleanup.
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
