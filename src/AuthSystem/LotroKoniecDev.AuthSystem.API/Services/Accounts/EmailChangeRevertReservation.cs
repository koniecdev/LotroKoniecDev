using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;

namespace LotroKoniecDev.AuthSystem.API.Services.Accounts;

/// <inheritdoc />
/// <remarks>
/// One indexed lookup, which registration runs on every attempt. <see cref="IEmailChangeRevertWindow"/>
/// decides how long the address stays held, so this and the erasure hold of #685 agree. The address-taken
/// refusal in <c>RevertEmailChange</c> covers the moment between the window closing and the token expiring.
/// </remarks>
internal sealed class EmailChangeRevertReservation : IEmailChangeRevertReservation
{
    private readonly AuthDbContext _db;
    private readonly ILookupNormalizer _keyNormalizer;
    private readonly IEmailChangeRevertWindow _revertWindow;
    private readonly TimeProvider _timeProvider;

    public EmailChangeRevertReservation(
        AuthDbContext db,
        ILookupNormalizer keyNormalizer,
        IEmailChangeRevertWindow revertWindow,
        TimeProvider timeProvider)
    {
        _db = db;
        _keyNormalizer = keyNormalizer;
        _revertWindow = revertWindow;
        _timeProvider = timeProvider;
    }

    public async Task<bool> IsReservedAsync(string email, Guid? exceptUserId, CancellationToken cancellationToken)
    {
        // The normalizer UserManager uses, so this and the callers' uniqueness checks agree on "the same
        // address".
        string? normalized = _keyNormalizer.NormalizeEmail(email);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        DateTimeOffset armedAfter = _revertWindow.LiveSince(_timeProvider.GetUtcNow());

        IQueryable<ApplicationUser> holders = _db.Users
            .Where(user => user.NormalizedEmailChangeRevertTo == normalized
                           // A row with no timestamp is not reserved; there is no honest value to
                           // backfill it with.
                           && user.EmailChangeRevertArmedAt != null
                           && user.EmailChangeRevertArmedAt > armedAfter);

        if (exceptUserId is { } requestingUserId)
        {
            holders = holders.Where(user => user.Id != requestingUserId);
        }

        return await holders.AnyAsync(cancellationToken);
    }
}
