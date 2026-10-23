using OpenIddict.Abstractions;

namespace LotroKoniecDev.AuthSystem.API.Services.Sessions;

/// <summary>
/// Every flow that ends website sessions goes through this class. The sign-in server's own cookie
/// sessions end through its cookie handler and the security stamp (ADR-0062).
/// </summary>
internal sealed partial class UserSessionRevoker : IUserSessionRevoker
{
    private const string AuthorizationsStep = "authorizations";
    private const string AuthorizationStep = "authorization";
    private const string TokensStep = "tokens";

    /// <summary>
    /// So a stuck database cannot hold the request forever. Per step, so one step never eats the other's.
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
        // Best effort, only logged. Authorizations first: OpenIddict refuses a refresh token whose
        // authorization is revoked, so a refresh between the two steps gets a dead token. The bulk token
        // update keeps the concurrency token, so a racing refresh can overwrite it, which makes the
        // authorization the real guard. A failed step never skips the other.
        long? revokedAuthorizations = await TryRevokeAsync(
            userId, AuthorizationsStep, _authorizationManager.RevokeBySubjectAsync, LogRevocationFailed);
        long? revokedTokens = await TryRevokeAsync(
            userId, TokensStep, _tokenManager.RevokeBySubjectAsync, LogRevocationFailed);

        if (revokedAuthorizations is long authorizationCount && revokedTokens is long tokenCount)
        {
            LogSessionsRevoked(_logger, userId, tokenCount, authorizationCount);
        }
    }

    public async Task RevokeSessionAsync(string authorizationId)
    {
        // The same best effort and the same order as RevokeAllAsync, for the same reasons.
        long? revokedAuthorizations = await TryRevokeAsync(
            authorizationId, AuthorizationStep, RevokeAuthorizationAsync, LogSessionRevocationFailed);
        long? revokedTokens = await TryRevokeAsync(
            authorizationId, TokensStep, _tokenManager.RevokeByAuthorizationIdAsync, LogSessionRevocationFailed);

        if (revokedAuthorizations is long authorizationCount && revokedTokens is long tokenCount)
        {
            LogSessionRevoked(_logger, authorizationId, tokenCount, authorizationCount);
        }
    }

    /// <summary>
    /// OpenIddict has no bulk revoke for one authorization, so this finds the row and revokes it.
    /// </summary>
    private async ValueTask<long> RevokeAuthorizationAsync(string authorizationId, CancellationToken cancellationToken)
    {
        object? authorization = await _authorizationManager.FindByIdAsync(authorizationId, cancellationToken);
        if (authorization is null)
        {
            return 0;
        }

        // TryRevokeAsync returns false instead of throwing, and only OpenIddict's log records it.
        // Throwing lets the failure reach our Error log, like a failure of RevokeAllAsync.
        if (!await _authorizationManager.TryRevokeAsync(authorization, cancellationToken))
        {
            throw new InvalidOperationException("OpenIddict could not revoke the authorization.");
        }

        return 1;
    }

    private async Task<long?> TryRevokeAsync(
        string identifier,
        string step,
        Func<string, CancellationToken, ValueTask<long>> revoke,
        Action<ILogger, Exception, string, string> logFailure)
    {
        using CancellationTokenSource timeLimit = new(TimeLimit, _timeProvider);

        try
        {
            return await revoke(identifier, timeLimit.Token);
        }
        catch (Exception ex)
        {
            logFailure(_logger, ex, step, identifier);
            return null;
        }
    }

    [LoggerMessage(EventId = EventIds.UserSessionsRevoked, Level = LogLevel.Information, Message = "Revoked all sessions for user {UserId}: {TokenCount} token row(s) and {AuthorizationCount} authorization row(s) updated")]
    private static partial void LogSessionsRevoked(ILogger logger, string userId, long tokenCount, long authorizationCount);

    [LoggerMessage(EventId = EventIds.UserSessionsRevocationFailed, Level = LogLevel.Error, Message = "Failed to revoke the {Step} of user {UserId}. The other step is not skipped, but a refresh token that neither step revoked stays usable until it expires.")]
    private static partial void LogRevocationFailed(ILogger logger, Exception exception, string step, string userId);

    [LoggerMessage(EventId = EventIds.SingleSessionRevoked, Level = LogLevel.Information, Message = "Revoked session {AuthorizationId}: {TokenCount} token row(s) and {AuthorizationCount} authorization row(s) updated")]
    private static partial void LogSessionRevoked(ILogger logger, string authorizationId, long tokenCount, long authorizationCount);

    [LoggerMessage(EventId = EventIds.SingleSessionRevocationFailed, Level = LogLevel.Error, Message = "Failed to revoke the {Step} of session {AuthorizationId}. The other step is not skipped, but a refresh token that neither step revoked stays usable until it expires.")]
    private static partial void LogSessionRevocationFailed(ILogger logger, Exception exception, string step, string authorizationId);
}
