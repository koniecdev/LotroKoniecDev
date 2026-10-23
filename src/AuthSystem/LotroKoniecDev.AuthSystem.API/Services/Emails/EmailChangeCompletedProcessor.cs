using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Services.Accounts;
using LotroKoniecDev.AuthSystem.API.Services.Gdpr;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.Identity;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

/// <summary>
/// Tells the new address it is now the login, and tells the old one what happened, with the undo link
/// (ADR-0048).
/// </summary>
/// <remarks>
/// The revert token is built from the payload, because the row no longer holds the previous address,
/// which is half of the token's purpose. Safe to run twice: a repeat sends the same notices with a fresh
/// token, or no link once a confirm back to the armed address has settled the chain (#684).
/// </remarks>
internal sealed partial class EmailChangeCompletedProcessor : IEmailMessageProcessor
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IEmailChangeEmailSender _emailChangeEmailSender;
    private readonly IEmailChangeRevertWindow _revertWindow;
    private readonly ILogger<EmailChangeCompletedProcessor> _logger;

    public EmailChangeCompletedProcessor(
        UserManager<ApplicationUser> userManager,
        IEmailChangeEmailSender emailChangeEmailSender,
        IEmailChangeRevertWindow revertWindow,
        ILogger<EmailChangeCompletedProcessor> logger)
    {
        _userManager = userManager;
        _emailChangeEmailSender = emailChangeEmailSender;
        _revertWindow = revertWindow;
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

    /// <returns>
    /// Success also when the account is gone or erased (see <see cref="IEmailMessageProcessor.ProcessAsync"/>).
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

        // A notice that arrives after the erasure must not mail the person's addresses again (ADR-0065).
        if (ErasedAccounts.Includes(user))
        {
            LogAccountErased(_logger, message.IdentityUserId);
            return Result.Success();
        }

        // Only the change that armed the undo carries its link: after A to B to C, the B-to-C message
        // sends none, so an attacker gets nothing to burn before the owner clicks (ADR-0048).
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

            // The old address first: if only one message gets through, it has to be the one that can undo.
            Result revertOfferResult = await _emailChangeEmailSender.SendChangedNoticeWithRevertAsync(
                user.Id,
                message.PreviousEmail,
                message.NewEmail,
                revertToken,
                _revertWindow.Lifespan,
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
        EventId = EventIds.EmailChangeDispatchAccountErased,
        Level = LogLevel.Information,
        Message = "Skipping e-mail change notices for user {UserId}: the account has been erased")]
    private static partial void LogAccountErased(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = EventIds.EmailChangeDispatchUserGone,
        Level = LogLevel.Information,
        Message = "Skipping e-mail change notices for user {UserId}: the account no longer exists")]
    private static partial void LogUserGone(ILogger logger, Guid userId);
}
