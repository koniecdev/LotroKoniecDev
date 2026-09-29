using OpenIddict.Abstractions;

namespace LotroKoniecDev.AuthSystem.API.Services.Sessions;

/// <summary>
/// The <see cref="IUserSessionRevoker"/> built on the OpenIddict token and authorization managers. Every
/// flow that ends all of a user's sessions goes through this one class.
/// </summary>
internal sealed partial class UserSessionRevoker : IUserSessionRevoker
{
    private const string AuthorizationsStep = "authorizations";
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
        long? revokedAuthorizations = await TryRevokeAsync(userId, AuthorizationsStep, _authorizationManager.RevokeBySubjectAsync);
        long? revokedTokens = await TryRevokeAsync(userId, TokensStep, _tokenManager.RevokeBySubjectAsync);

        if (revokedAuthorizations is long authorizationCount && revokedTokens is long tokenCount)
        {
            LogSessionsRevoked(_logger, userId, tokenCount, authorizationCount);
        }
    }

    private async Task<long?> TryRevokeAsync(
        string userId,
        string step,
        Func<string, CancellationToken, ValueTask<long>> revokeBySubject)
    {
        using CancellationTokenSource timeLimit = new(TimeLimit, _timeProvider);

        try
        {
            return await revokeBySubject(userId, timeLimit.Token);
        }
        catch (Exception ex)
        {
            LogRevocationFailed(_logger, ex, step, userId);
            return null;
        }
    }

    [LoggerMessage(EventId = EventIds.UserSessionsRevoked, Level = LogLevel.Information, Message = "Revoked all sessions for user {UserId}: {TokenCount} token row(s) and {AuthorizationCount} authorization row(s) updated")]
    private static partial void LogSessionsRevoked(ILogger logger, string userId, long tokenCount, long authorizationCount);

    [LoggerMessage(EventId = EventIds.UserSessionsRevocationFailed, Level = LogLevel.Error, Message = "Failed to revoke the {Step} of user {UserId}. The other step is not skipped, but a refresh token that neither step revoked stays usable until it expires.")]
    private static partial void LogRevocationFailed(ILogger logger, Exception exception, string step, string userId);
}
