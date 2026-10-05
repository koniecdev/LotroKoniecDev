using OpenIddict.Abstractions;

namespace LotroKoniecDev.AuthSystem.API.Services.Sessions;

/// <summary>
/// The <see cref="IUserSessionRevoker"/> built on the OpenIddict token and authorization managers. Every
/// flow that ends website sessions goes through this one class. The sign-in server's own cookie sessions
/// end through its cookie handler and the security stamp (ADR-0062).
/// </summary>
internal sealed partial class UserSessionRevoker : IUserSessionRevoker
{
    private const string AuthorizationsStep = "authorizations";
    private const string AuthorizationStep = "authorization";
    private const string TokensStep = "tokens";

    /// <summary>
    /// Stops a stuck database from holding the request and its connection forever. Each step gets its own
    /// limit, so a step that used up its time never takes the other step's.
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
        // Best effort: a failure here must not fail the caller, so it is only logged.
        // Authorizations go first, because OpenIddict refuses a refresh token whose authorization is
        // revoked: a refresh that lands between the two steps still gets a dead token. That also makes the
        // authorization the real guard, since a bulk update does not change a row's concurrency token and
        // a refresh racing it can save its own copy of a token row over the revoke. A step that fails
        // never skips the other one.
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

        // TryRevokeAsync does not throw when the update fails, for example on a database error, a
        // concurrency conflict or our own time limit. It returns false, and only OpenIddict's own log
        // records it. Throwing here lets that failure reach our Error log like a failure of RevokeAllAsync
        // does.
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
