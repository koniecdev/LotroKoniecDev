using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Services.Gdpr;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

/// <summary>
/// What happens when a <see cref="RegistrationAttemptedOnExistingAccount"/> message arrives: load the
/// account and tell its owner that someone tried to register with its address (#860).
/// </summary>
/// <remarks>
/// A message may arrive more than once (ADR-0035), so this has to be safe to run twice. It is: the notice
/// carries no state and no token, so at worst the owner reads it twice.
/// </remarks>
internal sealed partial class RegistrationAttemptedProcessor : IEmailMessageProcessor
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAccountConfirmationEmailSender _accountConfirmationEmailSender;
    private readonly ILogger<RegistrationAttemptedProcessor> _logger;

    public RegistrationAttemptedProcessor(
        UserManager<ApplicationUser> userManager,
        IAccountConfirmationEmailSender accountConfirmationEmailSender,
        ILogger<RegistrationAttemptedProcessor> logger)
    {
        _userManager = userManager;
        _accountConfirmationEmailSender = accountConfirmationEmailSender;
        _logger = logger;
    }

    public object? TryDeserialize(ReadOnlySpan<byte> body)
    {
        try
        {
            RegistrationAttemptedOnExistingAccount? message =
                JsonSerializer.Deserialize<RegistrationAttemptedOnExistingAccount>(body);
            return message is null || message.IdentityUserId == Guid.Empty ? null : message;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task<Result> ProcessAsync(object message, CancellationToken cancellationToken)
    {
        return ProcessAsync((RegistrationAttemptedOnExistingAccount)message, cancellationToken);
    }

    /// <summary>
    /// Handles one message from start to finish and says whether it may be acknowledged.
    /// </summary>
    /// <returns>
    /// This is not a business result. Success means "acknowledge it and drop it from the queue", either
    /// because the notice went out or because sending again could never change anything: the account is
    /// gone, erased, or has no address. Failure means "worth another try", for example when the SMTP relay
    /// is down.
    /// </returns>
    public async Task<Result> ProcessAsync(
        RegistrationAttemptedOnExistingAccount message,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ApplicationUser? user = await _userManager.FindByIdAsync(message.IdentityUserId.ToString());
        if (user is null)
        {
            LogUserGone(_logger, message.IdentityUserId);
            return Result.Success();
        }

        // An erasure that landed while the message waited leaves only the anonymized address, which no
        // mail reaches (ADR-0065).
        if (ErasedAccounts.Includes(user))
        {
            LogAccountErased(_logger, message.IdentityUserId);
            return Result.Success();
        }

        if (string.IsNullOrWhiteSpace(user.Email))
        {
            LogEmailMissing(_logger, message.IdentityUserId);
            return Result.Success();
        }

        return await _accountConfirmationEmailSender.SendRegistrationAttemptedNoticeAsync(
            user.Id,
            user.Email,
            cancellationToken);
    }

    [LoggerMessage(
        EventId = EventIds.RegistrationAttemptedNoticeUserGone,
        Level = LogLevel.Information,
        Message = "Skipping registration-attempt notice for user {UserId}: the account no longer exists")]
    private static partial void LogUserGone(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = EventIds.RegistrationAttemptedNoticeAccountErased,
        Level = LogLevel.Information,
        Message = "Skipping registration-attempt notice for user {UserId}: the account was erased")]
    private static partial void LogAccountErased(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = EventIds.RegistrationAttemptedNoticeAddressMissing,
        Level = LogLevel.Warning,
        Message = "Skipping registration-attempt notice for user {UserId}: the account carries no e-mail address")]
    private static partial void LogEmailMissing(ILogger logger, Guid userId);
}
