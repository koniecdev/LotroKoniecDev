using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.Identity;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

/// <summary>
/// What happens when an <see cref="EmailChangeCompleted"/> message arrives: tell the new address it
/// is now the login, and tell the old one what happened while handing it the link that undoes it
/// (ADR-0048).
/// </summary>
/// <remarks>
/// The revert token is created here, at send time, like every other token in this pipeline. It can be
/// built from the payload alone, which matters: by now the user row no longer holds the previous
/// address, and that address is half of the token's purpose.
/// Only the change that armed the undo carries a link. A later change in the same chain sends the
/// notices without one, so an attacker who moved the account on a second time gets nothing they could
/// use to undo the owner's recovery.
/// A message may arrive more than once (ADR-0035), so this has to be safe to run twice. It is: a
/// repeat sends the same notices with a fresh revert token that opens the same page and reaches the
/// same decision, and the armed target it reads does not move.
/// </remarks>
internal sealed partial class EmailChangeCompletedProcessor : IEmailMessageProcessor
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IEmailChangeEmailSender _emailChangeEmailSender;
    private readonly EmailChangeRevertTokenProviderOptions _revertTokenOptions;
    private readonly ILogger<EmailChangeCompletedProcessor> _logger;

    public EmailChangeCompletedProcessor(
        UserManager<ApplicationUser> userManager,
        IEmailChangeEmailSender emailChangeEmailSender,
        IOptions<EmailChangeRevertTokenProviderOptions> revertTokenOptions,
        ILogger<EmailChangeCompletedProcessor> logger)
    {
        _userManager = userManager;
        _emailChangeEmailSender = emailChangeEmailSender;
        _revertTokenOptions = revertTokenOptions.Value;
        _logger = logger;
    }

    public object? TryDeserialize(ReadOnlySpan<byte> body)
    {
        try
        {
            EmailChangeCompleted? message = JsonSerializer.Deserialize<EmailChangeCompleted>(body);

            return message is null
                   || message.IdentityUserId == Guid.Empty
                   || string.IsNullOrWhiteSpace(message.PreviousEmail)
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
        return ProcessAsync((EmailChangeCompleted)message, cancellationToken);
    }

    /// <summary>
    /// Handles one message from start to finish and says whether it may be acknowledged.
    /// </summary>
    /// <returns>
    /// This is not a business result. Success means "acknowledge it and drop it from the queue",
    /// either because both notices went out or because the account is gone and sending again could
    /// never change anything. Failure means "worth another try", for example when the SMTP relay is
    /// down.
    /// </returns>
    public async Task<Result> ProcessAsync(EmailChangeCompleted message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ApplicationUser? user = await _userManager.FindByIdAsync(message.IdentityUserId.ToString());
        if (user is null)
        {
            LogUserGone(_logger, message.IdentityUserId);
            return Result.Success();
        }

        // Only the change that armed the undo may carry its link. After A to B to C the armed target
        // is still A, so the B-to-C message sends no link at all — there is nothing to hand whoever
        // took the account over, and nothing for them to burn before the owner clicks (ADR-0048).
        bool armedByThisChange = string.Equals(
            _userManager.NormalizeEmail(user.EmailChangeRevertTo),
            _userManager.NormalizeEmail(message.PreviousEmail),
            StringComparison.Ordinal);

        if (armedByThisChange)
        {
            string revertToken = await _userManager.GenerateUserTokenAsync(
                user,
                EmailChangeRevertTokenProvider.ProviderName,
                EmailChangeRevertTokenProvider.PurposeFor(message.PreviousEmail, message.NewEmail));

            // The old address goes first. It is the one that can still undo this, so if only one of
            // the two messages ever gets through, it has to be that one.
            Result revertOfferResult = await _emailChangeEmailSender.SendChangedNoticeWithRevertAsync(
                user.Id,
                message.PreviousEmail,
                message.NewEmail,
                revertToken,
                _revertTokenOptions.TokenLifespan,
                cancellationToken);

            if (revertOfferResult.IsFailure)
            {
                return revertOfferResult;
            }
        }
        else
        {
            LogNoUndoArmed(_logger, message.IdentityUserId);
        }

        return await _emailChangeEmailSender.SendChangedNoticeAsync(
            user.Id, message.NewEmail, message.PreviousEmail, cancellationToken);
    }

    [LoggerMessage(
        EventId = EventIds.EmailChangeDispatchStaleRequest,
        Level = LogLevel.Information,
        Message = "No undo link for user {UserId}: an earlier change in the chain already armed one")]
    private static partial void LogNoUndoArmed(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = EventIds.EmailChangeDispatchUserGone,
        Level = LogLevel.Information,
        Message = "Skipping e-mail change notices for user {UserId}: the account no longer exists")]
    private static partial void LogUserGone(ILogger logger, Guid userId);
}
