using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.Identity;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

/// <summary>
/// What happens when an <see cref="EmailChangeRequested"/> message arrives: create the confirmation
/// token now rather than earlier, send the link to the new address, and warn the old one that
/// somebody asked to move the account (ADR-0048).
/// </summary>
/// <remarks>
/// A message may arrive more than once (ADR-0035), so this has to be safe to run twice. It is: a
/// repeat re-sends the same two messages with a fresh but equally valid token, which is annoying and
/// nothing worse.
/// The stale check matters more than it looks. The payload carries the address the account had when
/// the request was made, and a delivery that arrives after the change already happened would
/// otherwise read the new address off the row and send the "somebody wants to move your account"
/// warning to the very mailbox that asked for it.
/// </remarks>
internal sealed partial class EmailChangeRequestProcessor : IEmailMessageProcessor
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IEmailChangeEmailSender _emailChangeEmailSender;
    private readonly ILogger<EmailChangeRequestProcessor> _logger;

    public EmailChangeRequestProcessor(
        UserManager<ApplicationUser> userManager,
        IEmailChangeEmailSender emailChangeEmailSender,
        ILogger<EmailChangeRequestProcessor> logger)
    {
        _userManager = userManager;
        _emailChangeEmailSender = emailChangeEmailSender;
        _logger = logger;
    }

    public object? TryDeserialize(ReadOnlySpan<byte> body)
    {
        try
        {
            EmailChangeRequested? message = JsonSerializer.Deserialize<EmailChangeRequested>(body);

            return message is null
                   || message.IdentityUserId == Guid.Empty
                   || string.IsNullOrWhiteSpace(message.CurrentEmail)
                   || string.IsNullOrWhiteSpace(message.NewEmail)
                ? null
                : message;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task<Result> ProcessAsync(object message, CancellationToken cancellationToken)
    {
        return ProcessAsync((EmailChangeRequested)message, cancellationToken);
    }

    /// <summary>
    /// Handles one message from start to finish and says whether it may be acknowledged.
    /// </summary>
    /// <returns>
    /// This is not a business result. Success means "acknowledge it and drop it from the queue",
    /// either because both e-mails went out or because sending again could never change anything: the
    /// user is gone, or the request is out of date because the address already moved. Failure means
    /// "worth another try", for example when the SMTP relay is down.
    /// </returns>
    public async Task<Result> ProcessAsync(EmailChangeRequested message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ApplicationUser? user = await _userManager.FindByIdAsync(message.IdentityUserId.ToString());
        if (user is null)
        {
            LogUserGone(_logger, message.IdentityUserId);
            return Result.Success();
        }

        if (string.IsNullOrWhiteSpace(user.Email))
        {
            LogAddressMissing(_logger, message.IdentityUserId);
            return Result.Success();
        }

        if (!string.Equals(
                _userManager.NormalizeEmail(user.Email),
                _userManager.NormalizeEmail(message.CurrentEmail),
                StringComparison.Ordinal))
        {
            LogStaleRequest(_logger, message.IdentityUserId);
            return Result.Success();
        }

        string verificationToken = await _userManager.GenerateUserTokenAsync(
            user,
            EmailChangeTokenProvider.ProviderName,
            EmailChangeTokenProvider.PurposeFor(message.NewEmail));

        Result verificationResult = await _emailChangeEmailSender.SendVerificationAsync(
            user.Id, message.NewEmail, verificationToken, cancellationToken);

        if (verificationResult.IsFailure)
        {
            return verificationResult;
        }

        Result warningResult = await _emailChangeEmailSender.SendChangeRequestedWarningAsync(
            user.Id, message.CurrentEmail, message.NewEmail, cancellationToken);

        if (warningResult.IsFailure)
        {
            // Retrying re-sends the verification link too. A second copy of the same link is harmless,
            // and the warning is the message this flow cannot afford to lose.
            LogWarningFailed(_logger, user.Id, warningResult.Error.Message);
        }

        return warningResult;
    }

    [LoggerMessage(
        EventId = EventIds.EmailChangeDispatchUserGone,
        Level = LogLevel.Information,
        Message = "Skipping e-mail change messages for user {UserId}: the account no longer exists")]
    private static partial void LogUserGone(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = EventIds.EmailChangeDispatchStaleRequest,
        Level = LogLevel.Information,
        Message = "Skipping e-mail change messages for user {UserId}: the account already moved to another address")]
    private static partial void LogStaleRequest(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = EventIds.EmailChangeDispatchAddressMissing,
        Level = LogLevel.Warning,
        Message = "Skipping e-mail change messages for user {UserId}: the account carries no e-mail address")]
    private static partial void LogAddressMissing(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = EventIds.EmailChangeDispatchWarningFailed,
        Level = LogLevel.Error,
        Message = "Failed to warn the previous address of user {UserId} about a pending e-mail change: {Error}")]
    private static partial void LogWarningFailed(ILogger logger, Guid userId, string error);
}
