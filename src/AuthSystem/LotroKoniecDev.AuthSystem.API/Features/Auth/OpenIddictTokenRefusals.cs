using System.Security.Claims;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

/// <summary>
/// OpenIddict refuses an expired, used or revoked code or refresh token before <see cref="TokenEndpoint"/>
/// runs. Its own log line names the token, not the user (#977). These handlers note the token's user while
/// OpenIddict checks the token, and write one warning when OpenIddict then refuses the request. They never
/// change the answer to the client.
/// </summary>
internal static partial class OpenIddictTokenRefusals
{
    private static readonly string NoteKey = typeof(OpenIddictTokenRefusals).FullName!;

    /// <summary>
    /// Runs just before OpenIddict's expiry check, when the token is read and its type is known.
    /// </summary>
    internal sealed class NoteTokenUser : IOpenIddictServerHandler<ValidateTokenContext>
    {
        public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
            OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateTokenContext>()
                .UseSingletonHandler<NoteTokenUser>()
                .SetOrder(OpenIddictServerHandlers.Protection.ValidateExpirationDate.Descriptor.Order - 500)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        public ValueTask HandleAsync(ValidateTokenContext context)
        {
            if (context is { EndpointType: OpenIddictServerEndpointType.Token, Principal: ClaimsPrincipal principal }
                && TokenGrantName.ForTokenType(principal.GetTokenType()) is TokenGrantName grant
                && principal.GetClaim(Claims.Subject) is { Length: > 0 } userId)
            {
                context.Transaction.SetProperty(NoteKey, new TokenUserNote(grant, userId));
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Runs just after OpenIddict's expiry check. Nothing else runs between the two notes, so a refusal
    /// that comes before this one means the token has expired. OpenIddict's answer cannot tell: it says
    /// "no longer valid" for an expired token and for a revoked one alike.
    /// </summary>
    internal sealed class NoteLifetimeChecked : IOpenIddictServerHandler<ValidateTokenContext>
    {
        public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
            OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateTokenContext>()
                .UseSingletonHandler<NoteLifetimeChecked>()
                .SetOrder(OpenIddictServerHandlers.Protection.ValidateExpirationDate.Descriptor.Order + 500)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        public ValueTask HandleAsync(ValidateTokenContext context)
        {
            if (context.Transaction.GetProperty<TokenUserNote>(NoteKey) is TokenUserNote note)
            {
                note.LifetimeChecked = true;
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
        private readonly ILogger<TokenEndpoint> _logger;

        public WarnWhenRefused(ILogger<TokenEndpoint> logger)
        {
            _logger = logger;
        }

        public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
            OpenIddictServerHandlerDescriptor.CreateBuilder<ProcessErrorContext>()
                .UseSingletonHandler<WarnWhenRefused>()
                .SetOrder(OpenIddictServerHandlers.AttachErrorParameters.Descriptor.Order + 1000)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        public ValueTask HandleAsync(ProcessErrorContext context)
        {
            if (context.EndpointType is not OpenIddictServerEndpointType.Token
                || context.Transaction.GetProperty<TokenUserNote>(NoteKey) is not TokenUserNote note)
            {
                return ValueTask.CompletedTask;
            }

            if (note.LifetimeChecked)
            {
                LogRefusedByOpenIddict(_logger, note.Grant.Step, note.UserId, context.ErrorDescription ?? context.Error);
            }
            else
            {
                LogTokenExpired(_logger, note.Grant.Step, note.UserId, note.Grant.Token);
            }

            return ValueTask.CompletedTask;
        }

        [LoggerMessage(EventId = EventIds.TokenGrantRefusedTokenExpired, Level = LogLevel.Warning, Message = "{Step} refused for user {UserId}: the {Token} has expired")]
        private static partial void LogTokenExpired(ILogger logger, string step, string userId, string token);

        [LoggerMessage(EventId = EventIds.TokenGrantRefusedByOpenIddict, Level = LogLevel.Warning, Message = "{Step} refused for user {UserId} by OpenIddict: {Reason}")]
        private static partial void LogRefusedByOpenIddict(ILogger logger, string step, string userId, string? reason);
    }

    private sealed class TokenUserNote
    {
        public TokenUserNote(TokenGrantName grant, string userId)
        {
            Grant = grant;
            UserId = userId;
        }

        public TokenGrantName Grant { get; }

        public string UserId { get; }

        public bool LifetimeChecked { get; set; }
    }
}
