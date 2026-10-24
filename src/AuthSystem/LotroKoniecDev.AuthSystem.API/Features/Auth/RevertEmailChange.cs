using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using LotroKoniecDev.AuthSystem.API.ApiErrors;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Services.Sessions;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Identity;
using LotroKoniecDev.SharedKernel.Constants;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

/// <summary>
/// Undoes an e-mail change from the old mailbox (ADR-0048), for the case where somebody who knew the
/// password moved the account to an address they own.
/// </summary>
/// <remarks>
/// It clears the password, which is what let the change happen, and returns a fresh reset token, as
/// <see cref="CancelAccountDeletion"/> does. No notice of the revert goes to the address the account was
/// taken to: the owner is reading the page, and a notice would only warn an attacker. It also cancels a
/// scheduled deletion, whose cancel link went to the attacker's mailbox.
/// </remarks>
internal sealed partial class RevertEmailChange
{
    internal sealed record Command(
        string UserId,
        string PreviousEmail,
        string CurrentEmail,
        string Token,
        string? IpAddress,
        string? UserAgent) : ICommand<Result<RevertedEmailChange>>;

    internal sealed record RevertedEmailChange(string RestoredEmail, string PasswordResetToken);

    internal sealed class CommandValidator : AbstractValidator<Command>
    {
        public CommandValidator()
        {
            RuleFor(x => x.UserId)
                .NotEmpty().WithMessage("User ID is required.");

            // The id comes from a link, and a value that is not a GUID throws inside Identity's store.
            RuleFor(x => x.UserId)
                .Must(userId => Guid.TryParse(userId, out _))
                    .WithMessage("User ID must be a GUID.");

            RuleFor(x => x.PreviousEmail)
                .NotEmpty().WithMessage("A valid email address is required.")
                .MaximumLength(EmailConstants.MaxLength)
                    .WithMessage($"Email must not exceed {EmailConstants.MaxLength} characters.")
                .Matches(EmailConstants.RegexPattern)
                    .WithMessage("A valid email address is required.");

            RuleFor(x => x.CurrentEmail)
                .NotEmpty().WithMessage("A valid email address is required.")
                .MaximumLength(EmailConstants.MaxLength)
                    .WithMessage($"Email must not exceed {EmailConstants.MaxLength} characters.")
                .Matches(EmailConstants.RegexPattern)
                    .WithMessage("A valid email address is required.");

            RuleFor(x => x.Token)
                .NotEmpty().WithMessage("Revert token is required.");
        }
    }

    internal sealed partial class Handler : ICommandHandler<Command, Result<RevertedEmailChange>>
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly AuthDbContext _db;
        private readonly OutboxWriter _outboxWriter;
        private readonly IUserSessionRevoker _sessionRevoker;
        private readonly IValidator<Command> _validator;
        private readonly ILogger<Handler> _logger;

        public Handler(
            UserManager<ApplicationUser> userManager,
            AuthDbContext db,
            OutboxWriter outboxWriter,
            IUserSessionRevoker sessionRevoker,
            IValidator<Command> validator,
            ILogger<Handler> logger)
        {
            _userManager = userManager;
            _db = db;
            _outboxWriter = outboxWriter;
            _sessionRevoker = sessionRevoker;
            _validator = validator;
            _logger = logger;
        }

        public async ValueTask<Result<RevertedEmailChange>> Handle(
            Command command, CancellationToken cancellationToken)
        {
            ValidationResult validationResult = await _validator.ValidateAsync(command, cancellationToken);
            if (!validationResult.IsValid)
            {
                return Result.Failure<RevertedEmailChange>(AuthErrors.InvalidEmailChangeToken);
            }

            string previousEmail = command.PreviousEmail;
            string currentEmail = command.CurrentEmail;

            ApplicationUser? user = await _userManager.FindByIdAsync(command.UserId);
            if (user is null)
            {
                LogTokenInvalid(_logger, previousEmail.MaskEmail(), command.IpAddress, command.UserAgent);
                return Result.Failure<RevertedEmailChange>(AuthErrors.InvalidEmailChangeToken);
            }

            bool tokenValid = await _userManager.VerifyUserTokenAsync(
                user,
                EmailChangeRevertTokenProvider.ProviderName,
                EmailChangeRevertTokenProvider.PurposeFor(previousEmail, currentEmail),
                command.Token);

            if (!tokenValid)
            {
                LogTokenInvalid(_logger, previousEmail.MaskEmail(), command.IpAddress, command.UserAgent);
                return Result.Failure<RevertedEmailChange>(AuthErrors.InvalidEmailChangeToken);
            }

            // The row decides where the account goes, never the link: the token proves who is asking, the
            // armed target says where. A second change cannot arm a target, so it gives an attacker nothing.
            string? revertTarget = user.EmailChangeRevertTo;
            if (string.IsNullOrWhiteSpace(revertTarget))
            {
                LogAlreadySettled(_logger, user.Id);
                return Result.Failure<RevertedEmailChange>(AuthErrors.InvalidEmailChangeToken);
            }

            // Stops a second click clearing a password the owner has since reset.
            if (string.Equals(
                    _userManager.NormalizeEmail(user.Email),
                    _userManager.NormalizeEmail(revertTarget),
                    StringComparison.Ordinal))
            {
                LogAlreadySettled(_logger, user.Id);
                return Result.Failure<RevertedEmailChange>(AuthErrors.InvalidEmailChangeToken);
            }

            // When another account holds the address, the password must stay: clearing it would lock the
            // account out of both addresses.
            ApplicationUser? previousAddressOwner = await _userManager.FindByEmailAsync(revertTarget);
            if (previousAddressOwner is not null)
            {
                // This very account, back there after this request loaded it, for example through a second
                // submit of the same link (#869).
                if (previousAddressOwner.Id == user.Id)
                {
                    RevertedEmailChange? landed = await FindLandedRevertAsync(user.Id, revertTarget);
                    if (landed is not null)
                    {
                        await _sessionRevoker.RevokeAllAsync(user.Id.ToString());
                        return Result.Success(landed);
                    }

                    LogAlreadySettled(_logger, user.Id);
                    return Result.Failure<RevertedEmailChange>(AuthErrors.InvalidEmailChangeToken);
                }

                LogPreviousAddressTaken(_logger, user.Id);
                return Result.Failure<RevertedEmailChange>(AuthErrors.UserAlreadyExistsByEmail);
            }

            user.Email = revertTarget;
            user.EmailConfirmed = true;

            // Whoever moved the address knew the password. The reset flow now runs through the mailbox
            // that just proved ownership.
            user.PasswordHash = null;

            user.SecurityStamp = Guid.NewGuid().ToString();

            // Retires every revert link so far and ends the chain. That also releases the reservation
            // (#684): the account is back on that address, so ordinary uniqueness guards it.
            user.EmailChangeRevertStamp = Guid.NewGuid();
            user.DisarmEmailChangeRevert();

            bool cancelledADeletion = user.DeletionScheduledAt is not null;
            user.DeletionScheduledAt = null;
            user.LockoutEnd = null;
            user.AccessFailedCount = 0;

            // ADR-0031 announces every cancellation. In the same save, and to the restored address.
            if (cancelledADeletion)
            {
                _outboxWriter.Enqueue(new AccountDeletionCancelled(user.Id));
            }

            Result<IdentityResult> updateResult = await TryUpdateAsync(user);
            if (updateResult.IsFailure)
            {
                DiscardPendingChanges();
                return Result.Failure<RevertedEmailChange>(updateResult.Error);
            }

            if (!updateResult.Value.Succeeded)
            {
                DiscardPendingChanges();

                // Another save got in first. When it was a second submit of this link, the undo is done.
                RevertedEmailChange? landed = await FindLandedRevertAsync(user.Id, revertTarget);
                if (landed is not null)
                {
                    await _sessionRevoker.RevokeAllAsync(user.Id.ToString());
                    return Result.Success(landed);
                }

                string errors = string.Join(", ", updateResult.Value.Errors.Select(e => e.Description));
                LogRevertFailed(_logger, user.Id, errors);
                return Result.Failure<RevertedEmailChange>(AuthErrors.EmailChangeFailed(errors));
            }

            await _sessionRevoker.RevokeAllAsync(user.Id.ToString());

            // After the save: a token made before would die with the stamp change it travels with.
            string passwordResetToken = await _userManager.GeneratePasswordResetTokenAsync(user);

            LogReverted(
                _logger, user.Id, currentEmail.MaskEmail(), revertTarget.MaskEmail(), command.IpAddress, command.UserAgent);

            return Result.Success(new RevertedEmailChange(revertTarget, passwordResetToken));
        }

        /// <summary>
        /// Same reason as <see cref="ConfirmEmailChange"/>: OpenIddict shares this context, so a later save
        /// would commit a revert reported as failed.
        /// </summary>
        private void DiscardPendingChanges()
        {
            _db.ChangeTracker.Clear();
        }

        /// <summary>
        /// Same reasoning and corner case as <see cref="ConfirmEmailChange"/> (#864, #866).
        /// </summary>
        private async Task<Result<IdentityResult>> TryUpdateAsync(ApplicationUser user)
        {
            try
            {
                IdentityResult result = await _userManager.UpdateAsync(user);
                if (result.IsTakenEmail)
                {
                    LogRevertRace(_logger, null, user.Id);
                    return Result.Failure<IdentityResult>(AuthErrors.UserAlreadyExistsByEmail);
                }

                return Result.Success(result);
            }
            catch (DbUpdateException ex) when (ex.IsTakenEmail(_db.Model))
            {
                LogRevertRace(_logger, ex, user.Id);
                return Result.Failure<IdentityResult>(AuthErrors.UserAlreadyExistsByEmail);
            }
        }

        /// <summary>
        /// A landed undo leaves the account on its old address, disarmed and with no password. Only a
        /// caller whose token passed gets here, so it gets what the first submit got: a way into the
        /// password reset (#869).
        /// </summary>
        /// <remarks>
        /// The caller ends the sessions again after a hit: the first submit may still be ending them, or
        /// its revoke may have failed.
        /// </remarks>
        private async Task<RevertedEmailChange?> FindLandedRevertAsync(Guid userId, string revertTarget)
        {
            _db.ChangeTracker.Clear();

            ApplicationUser? stored = await _userManager.FindByIdAsync(userId.ToString());
            if (stored is not { EmailChangeRevertTo: null, PasswordHash: null }
                || !string.Equals(
                    _userManager.NormalizeEmail(stored.Email),
                    _userManager.NormalizeEmail(revertTarget),
                    StringComparison.Ordinal))
            {
                return null;
            }

            LogAlreadyReverted(_logger, userId);

            string passwordResetToken = await _userManager.GeneratePasswordResetTokenAsync(stored);
            return new RevertedEmailChange(revertTarget, passwordResetToken);
        }

        [LoggerMessage(EventId = EventIds.EmailChangeRevertAlreadyApplied, Level = LogLevel.Information, Message = "Revert for user {UserId} was already applied by an earlier submit of the same link")]
        private static partial void LogAlreadyReverted(ILogger logger, Guid userId);

        [LoggerMessage(EventId = EventIds.EmailChangeRevertTokenInvalid, Level = LogLevel.Warning, Message = "Invalid e-mail change revert token presented for {PreviousEmail}. IP: {IpAddress}, UserAgent: {UserAgent}")]
        private static partial void LogTokenInvalid(ILogger logger, string previousEmail, string? ipAddress, string? userAgent);

        [LoggerMessage(EventId = EventIds.EmailChangeRevertAlreadySettled, Level = LogLevel.Information, Message = "Revert link for user {UserId} refused: the account no longer sits on the address it was issued against")]
        private static partial void LogAlreadySettled(ILogger logger, Guid userId);

        [LoggerMessage(EventId = EventIds.EmailChangeRevertAddressTaken, Level = LogLevel.Warning, Message = "Revert link for user {UserId} refused: the previous address now belongs to another account")]
        private static partial void LogPreviousAddressTaken(ILogger logger, Guid userId);

        [LoggerMessage(EventId = EventIds.EmailChangeReverted, Level = LogLevel.Information, Message = "E-mail change reverted for user {UserId}: {CurrentEmail} -> {PreviousEmail}. IP: {IpAddress}, UserAgent: {UserAgent}")]
        private static partial void LogReverted(ILogger logger, Guid userId, string currentEmail, string previousEmail, string? ipAddress, string? userAgent);

        [LoggerMessage(EventId = EventIds.EmailChangeRevertFailed, Level = LogLevel.Error, Message = "Failed to revert the e-mail change for user {UserId}: {Errors}")]
        private static partial void LogRevertFailed(ILogger logger, Guid userId, string errors);

        [LoggerMessage(EventId = EventIds.EmailChangeRevertRace, Level = LogLevel.Warning, Message = "Revert for user {UserId} lost a race for the previous address")]
        private static partial void LogRevertRace(ILogger logger, Exception? exception, Guid userId);
    }
}
