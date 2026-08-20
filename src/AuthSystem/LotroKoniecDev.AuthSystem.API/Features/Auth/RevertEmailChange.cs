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
/// Undoes an e-mail change from the old mailbox (ADR-0048). It exists for one case: somebody who knew
/// the password moved the account to an address they own, and the real owner still reads the address
/// it came from.
/// </summary>
/// <remarks>
/// It also clears the password, because the password is what let the change happen in the first
/// place, and returns a fresh reset token that sends the visitor into the reset flow.
/// <see cref="CancelAccountDeletion"/> answers the same problem the same way.
/// No e-mail goes out afterwards. The person who needs to know is reading the page, and telling the
/// address the account was just taken from would only warn an attacker.
/// It also cancels a scheduled deletion. After an address change the cancel link of ADR-0031 was sent
/// to a mailbox the owner may no longer control, so refusing here would leave the account to be erased
/// by the very person it was taken from.
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

            // The id arrives from a query string, and Identity converts it to the key type before it
            // queries. A value that is not a GUID throws inside the store, which would turn a bad link
            // into a crash on a page somebody opened from their inbox.
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

            // Where the account goes is decided by the row, never by the link. The token proves who is
            // asking; the armed target says where they may put it. That is what makes a second change
            // useless to an attacker: it cannot arm a target of its own, so there is no link to hand
            // them and no way for one to move the account somewhere the owner did not start from.
            string? revertTarget = user.EmailChangeRevertTo;
            if (string.IsNullOrWhiteSpace(revertTarget))
            {
                LogAlreadySettled(_logger, user.Id);
                return Result.Failure<RevertedEmailChange>(AuthErrors.InvalidEmailChangeToken);
            }

            // Already back where it started, so there is nothing to undo. This is what stops a second
            // click clearing a password the owner has since reset.
            if (string.Equals(
                    _userManager.NormalizeEmail(user.Email),
                    _userManager.NormalizeEmail(revertTarget),
                    StringComparison.Ordinal))
            {
                LogAlreadySettled(_logger, user.Id);
                return Result.Failure<RevertedEmailChange>(AuthErrors.InvalidEmailChangeToken);
            }

            // Somebody may have registered the freed address in the meantime. Then there is nothing to
            // go back to, and the password must stay as it is: clearing it would lock the account out
            // of both addresses at once.
            ApplicationUser? previousAddressOwner = await _userManager.FindByEmailAsync(revertTarget);
            if (previousAddressOwner is not null)
            {
                LogPreviousAddressTaken(_logger, user.Id);
                return Result.Failure<RevertedEmailChange>(AuthErrors.UserAlreadyExistsByEmail);
            }

            user.Email = revertTarget;
            user.EmailConfirmed = true;

            // Whoever moved the address knew the password, so the password goes. Only the reset flow
            // brings the account back, and it now runs through the mailbox that just proved ownership.
            user.PasswordHash = null;

            user.SecurityStamp = Guid.NewGuid().ToString();

            // Retires every revert link issued so far and disarms the chain, so the next change starts
            // a fresh one from here.
            user.EmailChangeRevertStamp = Guid.NewGuid();
            user.EmailChangeRevertTo = null;

            // A deletion the same person may have scheduled is called off here. Its cancel link went to
            // the address the account was moved to, so leaving the schedule in place would hand the
            // erasure to whoever took the account over.
            bool cancelledADeletion = user.DeletionScheduledAt is not null;
            user.DeletionScheduledAt = null;
            user.LockoutEnd = null;
            user.AccessFailedCount = 0;

            // ADR-0031 wants every cancellation announced, and this is one. The notice rides the same
            // save as the change, so "cancelled but nobody told" cannot happen, and it reaches the
            // restored address rather than the one it was taken to.
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

                string errors = string.Join(", ", updateResult.Value.Errors.Select(e => e.Description));
                LogRevertFailed(_logger, user.Id, errors);
                return Result.Failure<RevertedEmailChange>(AuthErrors.EmailChangeFailed(errors));
            }

            if (cancelledADeletion)
            {
                _outboxWriter.NotifyEnqueuedCommitted();
            }

            await _sessionRevoker.RevokeAllAsync(user.Id.ToString(), cancellationToken);

            // Created after the save, from the stored stamp. A token made before it would be
            // invalidated by the very stamp change it travels with.
            string passwordResetToken = await _userManager.GeneratePasswordResetTokenAsync(user);

            LogReverted(
                _logger, user.Id, currentEmail.MaskEmail(), revertTarget.MaskEmail(), command.IpAddress, command.UserAgent);

            return Result.Success(new RevertedEmailChange(revertTarget, passwordResetToken));
        }

        /// <summary>
        /// A failed update never reaches the store, so this unit of work is still in the change
        /// tracker with the restored address and a cleared password on it. The context is shared with
        /// OpenIddict for the rest of the request, so a later save there would commit a revert this
        /// handler just reported as failed.
        /// </summary>
        private void DiscardPendingChanges()
        {
            _db.ChangeTracker.Clear();
        }

        /// <summary>
        /// Same reasoning as the confirm leg: the uniqueness check is a plain query, the unique index
        /// is the real arbiter, and its duplicate-key error is not one <c>UserStore.UpdateAsync</c>
        /// handles.
        /// </summary>
        private async Task<Result<IdentityResult>> TryUpdateAsync(ApplicationUser user)
        {
            try
            {
                return Result.Success(await _userManager.UpdateAsync(user));
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                LogRevertRace(_logger, ex, user.Id);
                return Result.Failure<IdentityResult>(AuthErrors.UserAlreadyExistsByEmail);
            }
        }

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
        private static partial void LogRevertRace(ILogger logger, Exception exception, Guid userId);
    }
}
