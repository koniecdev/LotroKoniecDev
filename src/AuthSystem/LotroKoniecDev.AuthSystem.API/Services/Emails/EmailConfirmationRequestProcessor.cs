using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

/// <summary>
/// What happens when an <see cref="EmailConfirmationRequested"/> message arrives: load the user,
/// create the confirmation token now rather than earlier (see the payload's remarks about token
/// lifetime), and send the e-mail. It is the first entry in the
/// <see cref="IEmailMessageProcessor"/> registry (ADR-0038).
/// </summary>
/// <remarks>
/// A message may arrive more than once (ADR-0035), so this has to be safe to run twice. Today it is
/// for free: a repeat finds <see cref="ApplicationUser.EmailConfirmed"/> already set, or at worst
/// sends the confirmation e-mail again, which is annoying but harmless. A new message type has to show
/// the same property before it may follow this pattern.
/// </remarks>
internal sealed partial class EmailConfirmationRequestProcessor : IEmailMessageProcessor
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAccountConfirmationEmailSender _accountConfirmationEmailSender;
    private readonly ILogger<EmailConfirmationRequestProcessor> _logger;

    public EmailConfirmationRequestProcessor(
        UserManager<ApplicationUser> userManager,
        IAccountConfirmationEmailSender accountConfirmationEmailSender,
        ILogger<EmailConfirmationRequestProcessor> logger)
    {
        _userManager = userManager;
        _accountConfirmationEmailSender = accountConfirmationEmailSender;
        _logger = logger;
    }

    public object? TryDeserialize(ReadOnlySpan<byte> body)
    {
        try
        {
            EmailConfirmationRequested? message = JsonSerializer.Deserialize<EmailConfirmationRequested>(body);
            return message is null || message.IdentityUserId == Guid.Empty ? null : message;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task<Result> ProcessAsync(object message, CancellationToken cancellationToken)
    {
        return ProcessAsync((EmailConfirmationRequested)message, cancellationToken);
    }

    /// <summary>
    /// Handles one message from start to finish and says whether it may be acknowledged.
    /// </summary>
    /// <returns>
    /// This is not a business result. It answers one question: does this message need to be sent
    /// again? Success means "acknowledge it and drop it from the queue", either because the e-mail went
    /// out or because sending again could never change anything: the user is gone, the address is
    /// already confirmed, or the account has no address. Refusing those would repeat the same message
    /// forever. Failure means "worth another try", for example when the SMTP relay is down, and the
    /// consumer then rejects and requeues it.
    /// </returns>
    public async Task<Result> ProcessAsync(EmailConfirmationRequested message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ApplicationUser? user = await _userManager.FindByIdAsync(message.IdentityUserId.ToString());
        if (user is null)
        {
            LogUserGone(_logger, message.IdentityUserId);
            return Result.Success();
        }

        if (user.EmailConfirmed)
        {
            LogAlreadyConfirmed(_logger, message.IdentityUserId);
            return Result.Success();
        }

        if (string.IsNullOrWhiteSpace(user.Email))
        {
            LogEmailMissing(_logger, message.IdentityUserId);
            return Result.Success();
        }

        string confirmationToken = await _userManager.GenerateEmailConfirmationTokenAsync(user);

        Result sendEmailConfirmationResult = await _accountConfirmationEmailSender.SendEmailConfirmationAsync(
            user.Id,
            user.Email,
            confirmationToken,
            cancellationToken);
        return sendEmailConfirmationResult;
    }

    [LoggerMessage(
        EventId = EventIds.EmailConfirmationUserGone,
        Level = LogLevel.Information,
        Message = "Skipping confirmation e-mail for user {UserId}: the account no longer exists")]
    private static partial void LogUserGone(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = EventIds.EmailConfirmationAlreadyConfirmed,
        Level = LogLevel.Debug,
        Message = "Skipping confirmation e-mail for user {UserId}: the address is already confirmed")]
    private static partial void LogAlreadyConfirmed(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = EventIds.EmailConfirmationAddressMissing,
        Level = LogLevel.Warning,
        Message = "Skipping confirmation e-mail for user {UserId}: the account carries no e-mail address")]
    private static partial void LogEmailMissing(ILogger logger, Guid userId);
}
