using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;

namespace LotroKoniecDev.AuthSystem.API.Services.Accounts;

/// <inheritdoc />
/// <remarks>
/// One indexed lookup on <see cref="ApplicationUser.NormalizedEmailChangeRevertTo"/>, which
/// registration runs on every attempt. How long the address stays held is
/// <see cref="IEmailChangeRevertWindow"/>'s call, so this reservation and the erasure hold of #685
/// cannot disagree about when an undo dies. The address-taken refusal in <c>RevertEmailChange</c> is
/// what still answers for the tail between the window closing and the token itself expiring.
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
        // The same normalizer UserManager.NormalizeEmail and FindByEmailAsync both delegate to, so
        // this lookup and the uniqueness check next to it in every caller agree on what "the same
        // address" means.
        string? normalized = _keyNormalizer.NormalizeEmail(email);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        DateTimeOffset armedAfter = _revertWindow.LiveSince(_timeProvider.GetUtcNow());

        IQueryable<ApplicationUser> holders = _db.Users
            .Where(user => user.NormalizedEmailChangeRevertTo == normalized
                           // A row armed before #684 has no timestamp. It is read as not reserved,
                           // which is the behavior it shipped under; nothing is backfilled, because
                           // there is no honest value to backfill it with.
                           && user.EmailChangeRevertArmedAt != null
                           && user.EmailChangeRevertArmedAt > armedAfter);

        if (exceptUserId is { } requestingUserId)
        {
            holders = holders.Where(user => user.Id != requestingUserId);
        }

        return await holders.AnyAsync(cancellationToken);
    }
}
