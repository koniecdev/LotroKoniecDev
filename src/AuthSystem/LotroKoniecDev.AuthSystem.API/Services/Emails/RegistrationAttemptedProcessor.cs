using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Services.Gdpr;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

/// <summary>
/// Tells an account's owner that someone tried to register with its address (#860).
/// </summary>
/// <remarks>
/// Safe to run twice: the notice carries no state or token, so at worst the owner reads it twice.
/// </remarks>
internal sealed partial class RegistrationAttemptedProcessor : IEmailMessageProcessor
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAccountConfirmationEmailSender _accountConfirmationEmailSender;
    private readonly ILogger<RegistrationAttemptedProcessor> _logger;

    public object? TryDeserialize(ReadOnlySpan<byte> body)
    {
        try
        {
            RegistrationAttemptedOnExistingAccount? message =
                JsonSerializer.Deserialize<RegistrationAttemptedOnExistingAccount>(body);
            return message is null
                   || message.IdentityUserId == Guid.Empty
                   || string.IsNullOrWhiteSpace(message.AttemptedEmail)
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
        return ProcessAsync((RegistrationAttemptedOnExistingAccount)message, cancellationToken);
    }

    /// <returns>
    /// Success also when the account is gone, erased, has no address or has moved off the tried address
    /// (see <see cref="IEmailMessageProcessor.ProcessAsync"/>).
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

        if (!string.Equals(
                _userManager.NormalizeEmail(user.Email),
                _userManager.NormalizeEmail(message.AttemptedEmail),
                StringComparison.Ordinal))
        {
            LogAddressMovedOn(_logger, message.IdentityUserId);
            return Result.Success();
        }

        return await _accountConfirmationEmailSender.SendRegistrationAttemptedNoticeAsync(
            user.Id,
            user.Email,
            cancellationToken);
    }

    public RegistrationAttemptedProcessor(
        UserManager<ApplicationUser> userManager,
        IAccountConfirmationEmailSender accountConfirmationEmailSender,
        ILogger<RegistrationAttemptedProcessor> logger)
    {
        _userManager = userManager;
        _accountConfirmationEmailSender = accountConfirmationEmailSender;
        _logger = logger;
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
        EventId = EventIds.RegistrationAttemptedNoticeAddressMovedOn,
        Level = LogLevel.Information,
        Message = "Skipping registration-attempt notice for user {UserId}: the account moved off the address that was tried")]
    private static partial void LogAddressMovedOn(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = EventIds.RegistrationAttemptedNoticeAddressMissing,
        Level = LogLevel.Warning,
        Message = "Skipping registration-attempt notice for user {UserId}: the account carries no e-mail address")]
    private static partial void LogEmailMissing(ILogger logger, Guid userId);
}
