using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

/// <summary>
/// No token here: the reset token travels in the cancel endpoint's response. If a deletion has been
/// scheduled again by the time this runs, "your account was kept" would be wrong, so nothing is sent
/// (ADR-0038 decision 2).
/// </summary>
/// <remarks>
/// Safe to run twice: the notice carries no state or token, so at worst the user reads it again.
/// </remarks>
internal sealed partial class AccountDeletionCancelledProcessor : IEmailMessageProcessor
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAccountDeletionEmailSender _accountDeletionEmailSender;
    private readonly ILogger<AccountDeletionCancelledProcessor> _logger;

    public AccountDeletionCancelledProcessor(
        UserManager<ApplicationUser> userManager,
        IAccountDeletionEmailSender accountDeletionEmailSender,
        ILogger<AccountDeletionCancelledProcessor> logger)
    {
        _userManager = userManager;
        _accountDeletionEmailSender = accountDeletionEmailSender;
        _logger = logger;
    }

    public object? TryDeserialize(ReadOnlySpan<byte> body)
    {
        try
        {
            AccountDeletionCancelled? message = JsonSerializer.Deserialize<AccountDeletionCancelled>(body);
            return message is null || message.IdentityUserId == Guid.Empty ? null : message;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task<Result> ProcessAsync(object message, CancellationToken cancellationToken)
    {
        return ProcessAsync((AccountDeletionCancelled)message, cancellationToken);
    }

    /// <returns>
    /// Success also when the user is gone, a deletion was scheduled again or the account has no address
    /// (see <see cref="IEmailMessageProcessor.ProcessAsync"/>).
    /// </returns>
    public async Task<Result> ProcessAsync(AccountDeletionCancelled message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ApplicationUser? user = await _userManager.FindByIdAsync(message.IdentityUserId.ToString());
        if (user is null)
        {
            LogUserGone(_logger, message.IdentityUserId);
            return Result.Success();
        }

        // Also stops a dead-letter replay after the erasure, which leaves DeletionScheduledAt set.
        if (user.DeletionScheduledAt is not null)
        {
            LogDeletionRescheduled(_logger, message.IdentityUserId);
            return Result.Success();
        }

        if (string.IsNullOrWhiteSpace(user.Email))
        {
            LogEmailMissing(_logger, message.IdentityUserId);
            return Result.Success();
        }

        Result sendDeletionCancelledResult = await _accountDeletionEmailSender.SendDeletionCancelledEmailAsync(
            user.Id,
            user.Email,
            cancellationToken);
        return sendDeletionCancelledResult;
    }

    [LoggerMessage(
        EventId = EventIds.DeletionCancelledUserGone,
        Level = LogLevel.Information,
        Message = "Skipping deletion-cancelled e-mail for user {UserId}: the account no longer exists")]
    private static partial void LogUserGone(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = EventIds.DeletionCancelledRescheduled,
        Level = LogLevel.Information,
        Message = "Skipping deletion-cancelled e-mail for user {UserId}: deletion is scheduled again")]
    private static partial void LogDeletionRescheduled(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = EventIds.DeletionCancelledAddressMissing,
        Level = LogLevel.Warning,
        Message = "Skipping deletion-cancelled e-mail for user {UserId}: the account carries no e-mail address")]
    private static partial void LogEmailMissing(ILogger logger, Guid userId);
}
