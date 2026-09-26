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
/// Applies an e-mail change once the owner of the new mailbox follows the link. The address,
/// <see cref="ApplicationUser.EmailConfirmed"/> and the new security stamp land in one save, together
/// with the outbox row that notifies both mailboxes and arms the revert link (ADR-0048).
/// </summary>
/// <remarks>
/// The single save is not a style choice. Identity runs with <c>RequireConfirmedEmail</c>, so an
/// address that lands without its confirmation flag would lock the user out of their own account.
/// The token is checked before anything is enqueued, which is why this flow uses its own token
/// provider instead of <c>UserManager.ChangeEmailAsync</c>.
/// </remarks>
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

            // The id arrives from a query string, and Identity converts it to the key type before it
            // queries. A value that is not a GUID throws inside the store, which would turn a bad link
            // into a crash on a page somebody opened from their inbox.
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

            // An unknown user, a tampered address and an expired token all answer the same way, so the
            // page cannot be used to learn anything about an account.
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

            // Checked only after the token, so a caller without one cannot tell this state apart from
            // any other refusal.
            if (user.DeletionScheduledAt is not null)
            {
                return Result.Failure(AuthErrors.DeletionAlreadyScheduled);
            }

            ApplicationUser? addressOwner = await _userManager.FindByEmailAsync(newEmail);
            if (addressOwner is not null)
            {
                return Result.Failure(AuthErrors.UserAlreadyExistsByEmail);
            }

            // Registration is not the only way onto a free address, so the reservation of #684 has to
            // hold here too. The caller's own armed address is excluded: going back to where the chain
            // started is the move this reservation protects, never one it blocks.
            bool reserved = await _revertReservation.IsReservedAsync(newEmail, user.Id, cancellationToken);
            if (reserved)
            {
                LogReservedAddressRefused(_logger, user.Id, newEmail.MaskEmail());
                return Result.Failure(AuthErrors.UserAlreadyExistsByEmail);
            }

            // The notice and the revert token are both built from this address, and an empty one would
            // produce a message its own processor refuses to read. Refuse here instead.
            if (string.IsNullOrWhiteSpace(user.Email))
            {
                return Result.Failure(AuthErrors.InvalidEmailChangeToken);
            }

            string previousEmail = user.Email;

            user.Email = newEmail;

            // The address and its confirmation flag have to move together. NormalizedEmail is left
            // alone on purpose: UpdateAsync recomputes it after validation and before the write, so
            // setting it here would only be overwritten.
            user.EmailConfirmed = true;

            // A new stamp ends every session and makes this link single-use. It is set inside the same
            // save that commits the outbox row (ADR-0038 decision 2), so the revert token the dispatch
            // processor creates moments later is built from the stamp that was actually stored.
            user.SecurityStamp = Guid.NewGuid().ToString();

            string? armedTarget = _userManager.NormalizeEmail(user.EmailChangeRevertTo);
            bool backOnTheArmedAddress = armedTarget is not null
                                         && string.Equals(
                                             armedTarget,
                                             _userManager.NormalizeEmail(newEmail),
                                             StringComparison.Ordinal);

            if (backOnTheArmedAddress)
            {
                // The account is where the chain started, so the chain is over and is settled exactly
                // as a revert settles it. Leaving it armed would freeze the reservation at the first
                // change's timestamp while a later change still minted a fresh 14-day link off it
                // (#684) — the link would then outlive the reservation that protects its address.
                // Only somebody reading the armed mailbox can reach this: the confirm link went there.
                user.DisarmEmailChangeRevert();
                user.EmailChangeRevertStamp = Guid.NewGuid();
            }
            else if (user.EmailChangeRevertTo is null)
            {
                // Only the first change since the last revert arms the undo, and it arms it at the
                // address the chain started from. A second change must not make itself a target: after
                // A to B to C that would hand an undo link to B, which is whoever took the account
                // over (ADR-0048). The timestamp starts the reservation that keeps that address out of
                // anyone else's hands for as long as the revert token lives (#684).
                // The key normalizer is always registered, so a non-empty address always normalizes
                // to a non-empty value. An armed target without its twin is unreservable, which is
                // the one state ArmEmailChangeRevert exists to refuse.
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

                string errors = string.Join(", ", updateResult.Value.Errors.Select(e => e.Description));
                LogUpdateFailed(_logger, user.Id, errors);
                return Result.Failure(AuthErrors.EmailChangeFailed(errors));
            }

            _outboxWriter.NotifyEnqueuedCommitted();

            await _sessionRevoker.RevokeAllAsync(user.Id.ToString(), cancellationToken);

            LogChangeApplied(
                _logger, user.Id, previousEmail.MaskEmail(), newEmail.MaskEmail(), command.IpAddress, command.UserAgent);

            return Result.Success();
        }

        /// <summary>
        /// The uniqueness check above is an ordinary query, so two requests can pass it at the same
        /// time. The unique index is what really decides, and <c>UserStore.UpdateAsync</c> only handles
        /// concurrency conflicts, so the duplicate-key error arrives here as a raw
        /// <see cref="DbUpdateException"/>. Only a duplicate on an e-mail index is a lost race; any other
        /// save error goes on up and becomes a 500 (#864). Identity's own check inside <c>UpdateAsync</c>
        /// can lose the same race first, and gets the same answer (#866).
        /// </summary>
        /// <remarks>
        /// A known corner case: if the answer to the commit is lost after the server really committed,
        /// the save retries, the retry fails on the row it already wrote, and the user is told the change
        /// failed although it landed. We accept that, as registration does (#845).
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
        /// A failed update never reaches the store, so both halves of this unit of work are still
        /// sitting in the change tracker: the outbox row as Added, and the user as Modified with the
        /// new address already on it. This context is shared with OpenIddict for the rest of the
        /// request, so a later save there would commit the change we just reported as failed, or
        /// announce one that never happened. Dropping the whole unit of work is the only version that
        /// leaves neither half behind. A save error that escapes as an exception skips this. That is safe,
        /// because the rest of that request only writes the error response and saves nothing.
        /// </summary>
        private void DiscardPendingChanges()
        {
            _db.ChangeTracker.Clear();
        }

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
