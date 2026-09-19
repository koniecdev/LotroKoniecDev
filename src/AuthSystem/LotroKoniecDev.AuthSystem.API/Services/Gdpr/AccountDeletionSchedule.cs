using System.Linq.Expressions;
using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Services.Accounts;
using LotroKoniecDev.AuthSystem.API.Settings;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.API.Services.Gdpr;

/// <inheritdoc />
internal sealed class AccountDeletionSchedule : IAccountDeletionSchedule
{
    private readonly IEmailChangeRevertWindow _revertWindow;
    private readonly GdprSettings _gdprSettings;

    public AccountDeletionSchedule(
        IEmailChangeRevertWindow revertWindow,
        IOptions<GdprSettings> gdprSettings)
    {
        _revertWindow = revertWindow;
        _gdprSettings = gdprSettings.Value;
    }

    public DateTimeOffset CancellableUntil(DateTimeOffset scheduledAt) =>
        scheduledAt + _gdprSettings.DeletionGracePeriod;

    public DateTimeOffset FinalizesAt(DateTimeOffset scheduledAt, DateTimeOffset? revertArmedAt)
    {
        DateTimeOffset afterGrace = CancellableUntil(scheduledAt);
        DateTimeOffset? undoExpiresAt = _revertWindow.ExpiresAt(revertArmedAt);

        return undoExpiresAt > afterGrace ? undoExpiresAt.Value : afterGrace;
    }

    public Expression<Func<ApplicationUser, bool>> IsDueBy(DateTimeOffset moment)
    {
        // "The later of the two dates has passed" is the same statement as "both have passed", and
        // this form is two column comparisons against constants, which the provider translates. The
        // unit tests check the two readings against each other over a grid of timestamps, because the
        // rule is written twice here and only one of the two ever decides whether data survives.
        DateTimeOffset scheduledBefore = moment - _gdprSettings.DeletionGracePeriod;
        DateTimeOffset armedBefore = _revertWindow.LiveSince(moment);

        return user => user.DeletionScheduledAt != null
                       && user.DeletionScheduledAt <= scheduledBefore
                       && (user.EmailChangeRevertArmedAt == null
                           || user.EmailChangeRevertArmedAt <= armedBefore);
    }
}
