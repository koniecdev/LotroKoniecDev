using System.Security.Claims;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

/// <summary>
/// OpenIddict refuses an expired, used or revoked code or refresh token before <see cref="TokenEndpoint"/>
/// runs, and so does any other request it refuses after reading the token, such as a wrong code_verifier.
/// Its own log line names the token, not the user (#977). These handlers note the token's user while
/// OpenIddict checks the token, and write one warning when OpenIddict then refuses the request. They never
/// change the answer to the client.
/// </summary>
internal static partial class OpenIddictTokenRefusals
{
    private static readonly string NoteKey = typeof(OpenIddictTokenRefusals).FullName!;

    /// <summary>
    /// Runs right after OpenIddict has read the token, before it checks it. OpenIddict answers "no longer
    /// valid" for an expired token and for a revoked one alike, so this works out expiry the same way
    /// OpenIddict's own check does, to keep the two cases apart in the log.
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
                bool expired = principal.GetExpirationDate() is DateTimeOffset expiresAt
                    && expiresAt + context.TokenValidationParameters.ClockSkew < context.Options.TimeProvider.GetUtcNow();

                context.Transaction.SetProperty(NoteKey, new TokenUserNote(grant, userId, expired));
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// OpenIddict raises this event only for a request it refuses itself. A refusal of our own goes out
    /// through <see cref="TokenEndpoint"/>, which already wrote its warning. The warning goes under the
    /// token endpoint's log category, so every refused code exchange and refresh sits in one place.
    /// </summary>
    internal sealed partial class WarnWhenRefused : IOpenIddictServerHandler<ProcessErrorContext>
    {
        private readonly IOpenIddictTokenManager _tokenManager;
        private readonly ILogger<TokenEndpoint> _logger;

        public WarnWhenRefused(IOpenIddictTokenManager tokenManager, ILogger<TokenEndpoint> logger)
        {
            _tokenManager = tokenManager;
            _logger = logger;
        }

        public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
            OpenIddictServerHandlerDescriptor.CreateBuilder<ProcessErrorContext>()
                .UseScopedHandler<WarnWhenRefused>()
                .SetOrder(OpenIddictServerHandlers.AttachErrorParameters.Descriptor.Order + 1000)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        public async ValueTask HandleAsync(ProcessErrorContext context)
        {
            if (context.EndpointType is not OpenIddictServerEndpointType.Token)
            {
                return;
            }

            TokenUserNote? note = context.Transaction.GetProperty<TokenUserNote>(NoteKey)
                ?? await NoteFromStoredRefreshTokenAsync(context.Request, context.CancellationToken);

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
        /// A refresh token is stored as a row, and the row names its user even when OpenIddict refused the
        /// token before reading it, for example after the encryption key changed. Codes are not stored
        /// that way, so an unreadable code names no one.
        /// </summary>
        private async ValueTask<TokenUserNote?> NoteFromStoredRefreshTokenAsync(
            OpenIddictRequest? request,
            CancellationToken cancellationToken)
        {
            if (request is null
                || !request.IsRefreshTokenGrantType()
                || request.RefreshToken is not { Length: > 0 } refreshToken
                || await _tokenManager.FindByReferenceIdAsync(refreshToken, cancellationToken) is not { } token
                || !await _tokenManager.HasTypeAsync(token, TokenTypeIdentifiers.RefreshToken, cancellationToken)
                || await _tokenManager.GetSubjectAsync(token, cancellationToken) is not { Length: > 0 } userId)
            {
                return null;
            }

            return new TokenUserNote(TokenGrantName.Refresh, userId, Expired: false);
        }

        [LoggerMessage(EventId = EventIds.TokenGrantRefusedTokenExpired, Level = LogLevel.Warning, Message = "{Step} refused for user {UserId}: the {Token} has expired")]
        private static partial void LogTokenExpired(ILogger logger, string step, string userId, string token);

        [LoggerMessage(EventId = EventIds.TokenGrantRefusedByOpenIddict, Level = LogLevel.Warning, Message = "{Step} refused for user {UserId} by OpenIddict: {Reason}")]
        private static partial void LogRefusedByOpenIddict(ILogger logger, string step, string userId, string? reason);
    }

    private sealed record TokenUserNote(TokenGrantName Grant, string UserId, bool Expired);
}
