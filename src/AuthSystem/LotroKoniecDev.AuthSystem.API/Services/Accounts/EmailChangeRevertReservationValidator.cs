using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;

namespace LotroKoniecDev.AuthSystem.API.Services.Accounts;

/// <summary>
/// Identity runs it on every create and update through <c>UserManager</c>, so no path that gives an account
/// an address can forget the reservation of #684 (ADR-0048 rule 4, #905). A write straight through the
/// context skips it.
/// </summary>
internal sealed class EmailChangeRevertReservationValidator : IUserValidator<ApplicationUser>
{
    internal const string ErrorCode = "EmailReservedForEmailChangeUndo";

    private readonly IEmailChangeRevertReservation _revertReservation;
    private readonly AuthDbContext _db;
    private readonly ILookupNormalizer _keyNormalizer;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public EmailChangeRevertReservationValidator(
        IEmailChangeRevertReservation revertReservation,
        AuthDbContext db,
        ILookupNormalizer keyNormalizer,
        IHttpContextAccessor httpContextAccessor)
    {
        _revertReservation = revertReservation;
        _db = db;
        _keyNormalizer = keyNormalizer;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<IdentityResult> ValidateAsync(UserManager<ApplicationUser> manager, ApplicationUser user)
    {
        // Refusing an empty address is the built-in validator's job.
        if (string.IsNullOrWhiteSpace(user.Email))
        {
            return IdentityResult.Success;
        }

        // A failed login, a lockout or a password change saves the account too. Those must neither pay for
        // the query nor fail on an address the account already holds.
        if (KeepsItsStoredAddress(user))
        {
            return IdentityResult.Success;
        }

        // Identity hands a validator no token, so the query stops with the request; the admin seed has no
        // request and waits.
        CancellationToken requestAborted = _httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None;

        // The account's own armed address is excluded: going back there is what the reservation protects.
        bool reserved = await _revertReservation.IsReservedAsync(user.Email, user.Id, requestAborted);
        if (!reserved)
        {
            return IdentityResult.Success;
        }

        // Worded like a taken address and never naming it: a caller that forgets to map the code must not
        // tell anyone more than a taken address would.
        return IdentityResult.Failed(new IdentityError
        {
            Code = ErrorCode,
            Description = "This e-mail address is already taken."
        });
    }

    /// <summary>
    /// True only for an account this context loaded and whose address did not change since. An account it
    /// does not track counts as new, so a doubt always asks.
    /// </summary>
    private bool KeepsItsStoredAddress(ApplicationUser user)
    {
        // Entry() would otherwise detect changes and mark the account Modified before the store attaches it,
        // which changes what that attach does.
        bool detectedChangesAutomatically = _db.ChangeTracker.AutoDetectChangesEnabled;
        _db.ChangeTracker.AutoDetectChangesEnabled = false;

        try
        {
            EntityEntry<ApplicationUser> entry = _db.Entry(user);
            if (entry.State is not (EntityState.Unchanged or EntityState.Modified))
            {
                return false;
            }

            string? storedEmail = entry.Property(account => account.Email).OriginalValue;

            return string.Equals(
                _keyNormalizer.NormalizeEmail(storedEmail),
                _keyNormalizer.NormalizeEmail(user.Email),
                StringComparison.Ordinal);
        }
        finally
        {
            _db.ChangeTracker.AutoDetectChangesEnabled = detectedChangesAutomatically;
        }
    }
}
