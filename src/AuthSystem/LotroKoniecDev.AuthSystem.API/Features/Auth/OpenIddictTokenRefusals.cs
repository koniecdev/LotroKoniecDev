using System.Security.Claims;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

/// <summary>
/// OpenIddict refuses an expired, used or revoked code or refresh token, or a wrong code_verifier,
/// before <see cref="TokenEndpoint"/> runs, and its own log line names the token, not the user (#977).
/// These handlers note the token's user and write one log line when OpenIddict refuses. They never
/// change the answer to the client.
/// </summary>
internal static partial class OpenIddictTokenRefusals
{
    private static readonly string NoteKey = typeof(OpenIddictTokenRefusals).FullName!;

    /// <summary>
    /// Runs after OpenIddict reads the token and before it checks it. OpenIddict answers "no longer valid"
    /// for an expired and a revoked token alike, so this works out expiry the same way, to keep the two
    /// apart in the log.
    /// </summary>
    internal sealed class NoteTokenUser : IOpenIddictServerHandler<ValidateTokenContext>
    {
        public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
            OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateTokenContext>()
                .UseSingletonHandler<NoteTokenUser>()
                .SetOrder(OpenIddictServerHandlers.Protection.ValidatePrincipal.Descriptor.Order + 500)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        public ValueTask HandleAsync(ValidateTokenContext context)
        {
            if (context is { EndpointType: OpenIddictServerEndpointType.Token, Principal: ClaimsPrincipal principal }
                && TokenGrantName.ForTokenType(principal.GetTokenType()) is TokenGrantName grant
                && principal.GetClaim(Claims.Subject) is { Length: > 0 } userId)
            {
                bool expired = !context.DisableLifetimeValidation
                    && principal.GetExpirationDate() is DateTimeOffset expiresAt
                    && expiresAt + context.TokenValidationParameters.ClockSkew < context.Options.TimeProvider.GetUtcNow();

                context.Transaction.SetProperty(NoteKey, new TokenUserNote(grant, userId, expired));
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// OpenIddict raises this event only for a request it refuses itself; our own refusals are logged by
    /// <see cref="TokenEndpoint"/>. Logged under the token endpoint's category, so every refused code
    /// exchange and refresh sits in one place.
    /// </summary>
    internal sealed partial class LogWhenRefused : IOpenIddictServerHandler<ProcessErrorContext>
    {
        private readonly IOpenIddictTokenManager _tokenManager;
        private readonly ILogger<TokenEndpoint> _logger;

        public LogWhenRefused(IOpenIddictTokenManager tokenManager, ILogger<TokenEndpoint> logger)
        {
            _tokenManager = tokenManager;
            _logger = logger;
        }

        public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
            OpenIddictServerHandlerDescriptor.CreateBuilder<ProcessErrorContext>()
                .AddFilter<OpenIddictServerHandlerFilters.RequireTokenRequest>()
                .UseScopedHandler<LogWhenRefused>()
                .SetOrder(OpenIddictServerHandlers.AttachErrorParameters.Descriptor.Order + 1000)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        public async ValueTask HandleAsync(ProcessErrorContext context)
        {
            TokenUserNote? note = context.Transaction.GetProperty<TokenUserNote>(NoteKey)
                ?? await NoteFromStoredTokenAsync(context.Request, context.CancellationToken);

            if (note is null)
            {
                return;
            }

            if (note.Expired)
            {
                LogTokenExpired(_logger, note.Grant.Step, note.UserId, note.Grant.Token);
            }
            else
            {
                LogRefusedByOpenIddict(_logger, note.Grant.Step, note.UserId, context.ErrorDescription ?? context.Error);
            }
        }

        /// <summary>
        /// A stored token row names its user even when OpenIddict refused the token before reading it, for
        /// example after the encryption key changed. A row of another token type names no one. A failed
        /// lookup only logs, so it never turns OpenIddict's refusal into a server error.
        /// </summary>
        private async ValueTask<TokenUserNote?> NoteFromStoredTokenAsync(
            OpenIddictRequest? request,
            CancellationToken cancellationToken)
        {
            (TokenGrantName Grant, string? ReferenceId)? presented = request switch
            {
                null => null,
                _ when request.IsAuthorizationCodeGrantType() => (TokenGrantName.CodeExchange, request.Code),
                _ when request.IsRefreshTokenGrantType() => (TokenGrantName.Refresh, request.RefreshToken),
                _ => null
            };

            if (presented is not ({ } grant, { Length: > 0 } referenceId))
            {
                return null;
            }

            try
            {
                if (await _tokenManager.FindByReferenceIdAsync(referenceId, cancellationToken) is not { } token
                    || !await _tokenManager.HasTypeAsync(token, grant.TokenType, cancellationToken)
                    || await _tokenManager.GetSubjectAsync(token, cancellationToken) is not { Length: > 0 } userId)
                {
                    return null;
                }

                return new TokenUserNote(grant, userId, Expired: false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogStoredTokenLookupFailed(_logger, exception, grant.Step);
                return null;
            }
        }

        // Information: a token runs out when the user has been away, which is normal (#977).
        [LoggerMessage(EventId = EventIds.TokenGrantRefusedTokenExpired, Level = LogLevel.Information, Message = "{Step} refused for user {UserId}: the {Token} has expired")]
        private static partial void LogTokenExpired(ILogger logger, string step, string userId, string token);

        // A Warning, although a revoked token is normal: the same line covers a reused refresh token and a
        // wrong code_verifier, and both can mean a stolen token (#977).
        [LoggerMessage(EventId = EventIds.TokenGrantRefusedByOpenIddict, Level = LogLevel.Warning, Message = "{Step} refused for user {UserId} by OpenIddict: {Reason}")]
        private static partial void LogRefusedByOpenIddict(ILogger logger, string step, string userId, string? reason);

        [LoggerMessage(EventId = EventIds.TokenGrantRefusalUserLookupFailed, Level = LogLevel.Warning, Message = "{Step} refused by OpenIddict, and the stored token could not be read to name its user")]
        private static partial void LogStoredTokenLookupFailed(ILogger logger, Exception exception, string step);
    }

    private sealed record TokenUserNote(TokenGrantName Grant, string UserId, bool Expired);
}
