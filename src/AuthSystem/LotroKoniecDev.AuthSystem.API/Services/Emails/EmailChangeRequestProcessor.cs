using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Services.Accounts;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.Identity;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

/// <summary>
/// What happens when an <see cref="EmailChangeRequested"/> message arrives: warn the old address that
/// somebody asked to move the account, then create the confirmation token now rather than earlier and
/// send the link to the new address (ADR-0048).
/// </summary>
/// <remarks>
/// The request never looks the new address up (#860), so this is where it is decided. A new address
/// with an account gets a notice to its owner instead of the link, and a reserved one (#684) gets
/// nothing. The warning goes out first and on every branch: the requester reads that inbox, so a
/// missing or late warning would tell them which branch ran.
/// A message may arrive more than once (ADR-0035), so this has to be safe to run twice. It is: a
/// repeat re-sends the same messages with a fresh but equally valid token, which is annoying and
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
    private readonly IEmailChangeRevertReservation _revertReservation;
    private readonly ILogger<EmailChangeRequestProcessor> _logger;

    public EmailChangeRequestProcessor(
        UserManager<ApplicationUser> userManager,
        IEmailChangeEmailSender emailChangeEmailSender,
        IEmailChangeRevertReservation revertReservation,
        ILogger<EmailChangeRequestProcessor> logger)
    {
        _userManager = userManager;
        _emailChangeEmailSender = emailChangeEmailSender;
        _revertReservation = revertReservation;
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
    /// either because the e-mails went out or because sending again could never change anything: the
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

        // The warning first. It is the message this flow cannot afford to lose, so if only one mail gets
        // through it has to be this one. A retry after a failed link re-sends it, which is harmless.
        Result warningResult = await _emailChangeEmailSender.SendChangeRequestedWarningAsync(
            user.Id, message.CurrentEmail, message.NewEmail, cancellationToken);

        if (warningResult.IsFailure)
        {
            LogWarningFailed(_logger, user.Id, warningResult.Error.Message);
            return warningResult;
        }

        // The confirm leg refuses a taken or reserved address anyway, so a link there could never land.
        ApplicationUser? addressOwner = await _userManager.FindByEmailAsync(message.NewEmail);
        if (addressOwner is { Email: { } ownerEmail } && addressOwner.Id != user.Id)
        {
            LogAddressTaken(_logger, user.Id, addressOwner.Id);
            return await _emailChangeEmailSender.SendAddressTakenNoticeAsync(
                addressOwner.Id, ownerEmail, cancellationToken);
        }

        // A reserved address has no account, so there is nobody to tell (#684).
        if (await _revertReservation.IsReservedAsync(message.NewEmail, user.Id, cancellationToken))
        {
            LogAddressReserved(_logger, user.Id);
            return Result.Success();
        }

        string verificationToken = await _userManager.GenerateUserTokenAsync(
            user,
            EmailChangeTokenProvider.ProviderName,
            EmailChangeTokenProvider.PurposeFor(message.NewEmail));

        return await _emailChangeEmailSender.SendVerificationAsync(
            user.Id, message.NewEmail, verificationToken, cancellationToken);
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
        EventId = EventIds.EmailChangeDispatchAddressTaken,
        Level = LogLevel.Information,
        Message = "No e-mail change link for user {UserId}: the new address belongs to user {OwnerUserId}, who gets a notice instead")]
    private static partial void LogAddressTaken(ILogger logger, Guid userId, Guid ownerUserId);

    [LoggerMessage(
        EventId = EventIds.EmailChangeDispatchAddressReserved,
        Level = LogLevel.Information,
        Message = "No e-mail change link for user {UserId}: the new address is still reserved as another account's undo target")]
    private static partial void LogAddressReserved(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = EventIds.EmailChangeDispatchWarningFailed,
        Level = LogLevel.Error,
        Message = "Failed to warn the previous address of user {UserId} about a pending e-mail change: {Error}")]
    private static partial void LogWarningFailed(ILogger logger, Guid userId, string error);
}
