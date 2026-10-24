using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using LotroKoniecDev.AuthSystem.API.ApiErrors;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Services.Accounts;
using LotroKoniecDev.AuthSystem.API.Services.Sessions;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Identity;
using LotroKoniecDev.SharedKernel.Constants;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

/// <summary>
/// The address, <see cref="ApplicationUser.EmailConfirmed"/>, the new security stamp and the outbox row
/// that notifies both mailboxes land in one save (ADR-0048). Identity runs with
/// <c>RequireConfirmedEmail</c>, so an address that landed without its flag would lock the user out.
/// </summary>
internal sealed partial class ConfirmEmailChange
{
    internal sealed record Command(
        string UserId,
        string NewEmail,
        string Token,
        string? IpAddress,
        string? UserAgent) : ICommand<Result>;

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

            RuleFor(x => x.NewEmail)
                .NotEmpty().WithMessage("A valid email address is required.")
                .MaximumLength(EmailConstants.MaxLength)
                    .WithMessage($"Email must not exceed {EmailConstants.MaxLength} characters.")
                .Matches(EmailConstants.RegexPattern)
                    .WithMessage("A valid email address is required.");

            RuleFor(x => x.Token)
                .NotEmpty().WithMessage("Confirmation token is required.");
        }
    }

    internal sealed partial class Handler : ICommandHandler<Command, Result>
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly AuthDbContext _db;
        private readonly OutboxWriter _outboxWriter;
        private readonly IUserSessionRevoker _sessionRevoker;
        private readonly IEmailChangeRevertReservation _revertReservation;
        private readonly TimeProvider _timeProvider;
        private readonly IValidator<Command> _validator;
        private readonly ILogger<Handler> _logger;

        public Handler(
            UserManager<ApplicationUser> userManager,
            AuthDbContext db,
            OutboxWriter outboxWriter,
            IUserSessionRevoker sessionRevoker,
            IEmailChangeRevertReservation revertReservation,
            TimeProvider timeProvider,
            IValidator<Command> validator,
            ILogger<Handler> logger)
        {
            _userManager = userManager;
            _db = db;
            _outboxWriter = outboxWriter;
            _sessionRevoker = sessionRevoker;
            _revertReservation = revertReservation;
            _timeProvider = timeProvider;
            _validator = validator;
            _logger = logger;
        }

        public async ValueTask<Result> Handle(Command command, CancellationToken cancellationToken)
        {
            ValidationResult validationResult = await _validator.ValidateAsync(command, cancellationToken);
            if (!validationResult.IsValid)
            {
                return Result.Failure(AuthErrors.InvalidEmailChangeToken);
            }

            string newEmail = command.NewEmail;

            ApplicationUser? user = await _userManager.FindByIdAsync(command.UserId);

            // An unknown user, a tampered address and an expired token answer the same way, so the page
            // reveals nothing about an account.
            if (user is null)
            {
                LogTokenInvalid(_logger, newEmail.MaskEmail(), command.IpAddress, command.UserAgent);
                return Result.Failure(AuthErrors.InvalidEmailChangeToken);
            }

            // The target address is baked into the purpose, so editing it in the link fails here.
            bool tokenValid = await _userManager.VerifyUserTokenAsync(
                user,
                EmailChangeTokenProvider.ProviderName,
                EmailChangeTokenProvider.PurposeFor(newEmail),
                command.Token);

            if (!tokenValid)
            {
                LogTokenInvalid(_logger, newEmail.MaskEmail(), command.IpAddress, command.UserAgent);
                return Result.Failure(AuthErrors.InvalidEmailChangeToken);
            }

            // Only after the token, so a caller without one cannot tell this state from any other refusal.
            if (user.DeletionScheduledAt is not null)
            {
                return Result.Failure(AuthErrors.DeletionAlreadyScheduled);
            }

            ApplicationUser? addressOwner = await _userManager.FindByEmailAsync(newEmail);
            if (addressOwner is not null)
            {
                // A second submit of the same link, such as a double click, finds this very account (#869).
                if (addressOwner.Id == user.Id)
                {
                    if (await IsAlreadyAppliedAsync(user.Id, newEmail))
                    {
                        await _sessionRevoker.RevokeAllAsync(user.Id.ToString());
                        return Result.Success();
                    }

                    // Something else, such as an undo, moved the account here and rotated the stamp, so
                    // this link is spent.
                    LogTokenInvalid(_logger, newEmail.MaskEmail(), command.IpAddress, command.UserAgent);
                    return Result.Failure(AuthErrors.InvalidEmailChangeToken);
                }

                return Result.Failure(AuthErrors.UserAlreadyExistsByEmail);
            }

            // The reservation (#684) holds here too. The caller's own armed address is excluded: going
            // back to where the chain started is what the reservation protects.
            bool reserved = await _revertReservation.IsReservedAsync(newEmail, user.Id, cancellationToken);
            if (reserved)
            {
                LogReservedAddressRefused(_logger, user.Id, newEmail.MaskEmail());
                return Result.Failure(AuthErrors.UserAlreadyExistsByEmail);
            }

            // The notice and the revert token are built from this address; its processor refuses an empty one.
            if (string.IsNullOrWhiteSpace(user.Email))
            {
                return Result.Failure(AuthErrors.InvalidEmailChangeToken);
            }

            string previousEmail = user.Email;

            user.Email = newEmail;

            // NormalizedEmail is left alone: UpdateAsync recomputes it before the write.
            user.EmailConfirmed = true;

            // Ends every session and makes this link single-use. In the same save as the outbox row
            // (ADR-0038 decision 2), so the dispatch builds the revert token from the stored stamp.
            user.SecurityStamp = Guid.NewGuid().ToString();

            string? armedTarget = _userManager.NormalizeEmail(user.EmailChangeRevertTo);
            bool backOnTheArmedAddress = armedTarget is not null
                                         && string.Equals(
                                             armedTarget,
                                             _userManager.NormalizeEmail(newEmail),
                                             StringComparison.Ordinal);

            if (backOnTheArmedAddress)
            {
                // Back where the chain started, so it is settled as a revert settles it. Left armed, a
                // later change would mint a fresh 14-day link that outlives the reservation (#684). Only the
                // reader of the armed mailbox can get here: the confirm link went there.
                user.DisarmEmailChangeRevert();
                user.EmailChangeRevertStamp = Guid.NewGuid();
            }
            else if (user.EmailChangeRevertTo is null)
            {
                // Only the first change since the last revert arms the undo: after A to B to C, an undo to
                // B would help whoever took the account over (ADR-0048). The key normalizer is always
                // registered, so a non-empty address never normalizes to null.
                string normalizedPreviousEmail = _userManager.NormalizeEmail(previousEmail)!;

                user.ArmEmailChangeRevert(
                    previousEmail, normalizedPreviousEmail, _timeProvider.GetUtcNow());
            }

            _outboxWriter.Enqueue(new EmailChangeCompleted(user.Id, previousEmail, newEmail));

            Result<IdentityResult> updateResult = await TryUpdateAsync(user);
            if (updateResult.IsFailure)
            {
                DiscardPendingChanges();
                return Result.Failure(updateResult.Error);
            }

            if (!updateResult.Value.Succeeded)
            {
                DiscardPendingChanges();

                // Another save got in first. When it was a second submit of this link, the change is there.
                if (await IsAlreadyAppliedAsync(user.Id, newEmail))
                {
                    await _sessionRevoker.RevokeAllAsync(user.Id.ToString());
                    return Result.Success();
                }

                string errors = string.Join(", ", updateResult.Value.Errors.Select(e => e.Description));
                LogUpdateFailed(_logger, user.Id, errors);
                return Result.Failure(AuthErrors.EmailChangeFailed(errors));
            }

            await _sessionRevoker.RevokeAllAsync(user.Id.ToString());

            LogChangeApplied(
                _logger, user.Id, previousEmail.MaskEmail(), newEmail.MaskEmail(), command.IpAddress, command.UserAgent);

            return Result.Success();
        }

        /// <summary>
        /// Two requests can pass the uniqueness query at once, and the index then throws a raw
        /// <see cref="DbUpdateException"/>, which <c>UserStore.UpdateAsync</c> does not handle. Only a
        /// duplicate on an e-mail index is a lost race; any other error becomes a 500 (#864). Identity's
        /// own check inside <c>UpdateAsync</c> can lose the same race first (#866).
        /// </summary>
        /// <remarks>
        /// Accepted corner case, as in registration (#845): if the commit's answer is lost after it landed,
        /// the retry fails on its own row and the user is told the change failed.
        /// </remarks>
        private async Task<Result<IdentityResult>> TryUpdateAsync(ApplicationUser user)
        {
            try
            {
                IdentityResult result = await _userManager.UpdateAsync(user);
                if (result.IsTakenEmail)
                {
                    LogUpdateRace(_logger, null, user.Id);
                    return Result.Failure<IdentityResult>(AuthErrors.UserAlreadyExistsByEmail);
                }

                return Result.Success(result);
            }
            catch (DbUpdateException ex) when (ex.IsTakenEmail(_db.Model))
            {
                LogUpdateRace(_logger, ex, user.Id);
                return Result.Failure<IdentityResult>(AuthErrors.UserAlreadyExistsByEmail);
            }
        }

        /// <summary>
        /// After a failed update the outbox row and the modified user are still tracked, and OpenIddict
        /// shares this context for the rest of the request, so a later save would commit a change we
        /// reported as failed. A save error that escapes as an exception skips this safely: that request
        /// saves nothing more.
        /// </summary>
        private void DiscardPendingChanges()
        {
            _db.ChangeTracker.Clear();
        }

        /// <summary>
        /// Reads the stored account and says whether this change already landed. Only a caller whose token
        /// passed gets here, so "done" reveals nothing (#869). An undo can also put the account on this
        /// address, but it clears the password and a confirm never does.
        /// </summary>
        /// <remarks>
        /// The caller ends the sessions again after a "yes": the first submit may still be ending them,
        /// or its revoke may have failed.
        /// </remarks>
        private async Task<bool> IsAlreadyAppliedAsync(Guid userId, string newEmail)
        {
            _db.ChangeTracker.Clear();

            ApplicationUser? stored = await _userManager.FindByIdAsync(userId.ToString());
            if (stored is not { EmailConfirmed: true, PasswordHash: not null }
                || !string.Equals(
                    _userManager.NormalizeEmail(stored.Email),
                    _userManager.NormalizeEmail(newEmail),
                    StringComparison.Ordinal))
            {
                return false;
            }

            LogAlreadyApplied(_logger, userId);
            return true;
        }

        [LoggerMessage(EventId = EventIds.EmailChangeAlreadyApplied, Level = LogLevel.Information, Message = "E-mail change for user {UserId} was already applied by an earlier submit of the same link")]
        private static partial void LogAlreadyApplied(ILogger logger, Guid userId);

        [LoggerMessage(EventId = EventIds.EmailChangeTokenInvalid, Level = LogLevel.Warning, Message = "Invalid e-mail change token presented for {NewEmail}. IP: {IpAddress}, UserAgent: {UserAgent}")]
        private static partial void LogTokenInvalid(ILogger logger, string newEmail, string? ipAddress, string? userAgent);

        [LoggerMessage(EventId = EventIds.EmailChangeApplied, Level = LogLevel.Information, Message = "E-mail change applied for user {UserId}: {PreviousEmail} -> {NewEmail}. IP: {IpAddress}, UserAgent: {UserAgent}")]
        private static partial void LogChangeApplied(ILogger logger, Guid userId, string previousEmail, string newEmail, string? ipAddress, string? userAgent);

        [LoggerMessage(EventId = EventIds.EmailChangeUpdateFailed, Level = LogLevel.Error, Message = "Failed to persist the e-mail change for user {UserId}: {Errors}")]
        private static partial void LogUpdateFailed(ILogger logger, Guid userId, string errors);

        [LoggerMessage(EventId = EventIds.EmailChangeConfirmRace, Level = LogLevel.Warning, Message = "E-mail change for user {UserId} lost a race for the new address")]
        private static partial void LogUpdateRace(ILogger logger, Exception? exception, Guid userId);

        [LoggerMessage(EventId = EventIds.EmailChangeConfirmAddressReserved, Level = LogLevel.Warning, Message = "E-mail change for user {UserId} refused: {NewEmail} is still reserved as another account's undo target")]
        private static partial void LogReservedAddressRefused(ILogger logger, Guid userId, string newEmail);
    }
}
