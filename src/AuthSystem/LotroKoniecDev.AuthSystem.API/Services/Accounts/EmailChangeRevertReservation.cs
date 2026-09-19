using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Identity;

namespace LotroKoniecDev.AuthSystem.API.Services.Accounts;

/// <inheritdoc />
/// <remarks>
/// One indexed lookup on <see cref="ApplicationUser.NormalizedEmailChangeRevertTo"/>, which
/// registration runs on every attempt. The window is the revert token's own lifespan measured from
/// <see cref="ApplicationUser.EmailChangeRevertArmedAt"/>, so no address is ever blocked for longer
/// than the link that needs it. The token is minted a moment later, by the outbox processor, so the
/// reservation closes marginally before the token does; the address-taken refusal in
/// <c>RevertEmailChange</c> is what still answers for that tail.
/// </remarks>
internal sealed class EmailChangeRevertReservation : IEmailChangeRevertReservation
{
    private readonly AuthDbContext _db;
    private readonly ILookupNormalizer _keyNormalizer;
    private readonly TimeProvider _timeProvider;
    private readonly EmailChangeRevertTokenProviderOptions _revertTokenOptions;

    public EmailChangeRevertReservation(
        AuthDbContext db,
        ILookupNormalizer keyNormalizer,
        TimeProvider timeProvider,
        IOptions<EmailChangeRevertTokenProviderOptions> revertTokenOptions)
    {
        _db = db;
        _keyNormalizer = keyNormalizer;
        _timeProvider = timeProvider;
        _revertTokenOptions = revertTokenOptions.Value;
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

        DateTimeOffset armedAfter = _timeProvider.GetUtcNow() - _revertTokenOptions.TokenLifespan;

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
