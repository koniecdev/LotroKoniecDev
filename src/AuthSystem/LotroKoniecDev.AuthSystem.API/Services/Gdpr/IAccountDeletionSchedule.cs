using System.Linq.Expressions;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.API.Services.Gdpr;

/// <summary>
/// The one place the deletion dates are worked out (ADR-0031, #685, #947, #916). The response header,
/// the e-mail, the login message and the finalizer's query all read it, so the date a user is promised
/// is the date the finalizer keeps.
/// </summary>
internal interface IAccountDeletionSchedule
{
    /// <summary>
    /// The scheduled time plus the grace period. A token from a late e-mail outlives this date, and the
    /// cancel endpoint refuses it anyway (#947).
    /// </summary>
    DateTimeOffset CancellableUntil(DateTimeOffset scheduledAt);

    /// <summary>
    /// The link is already dead at <see cref="CancellableUntil"/> itself, the same boundary
    /// <see cref="IsDueBy"/> uses.
    /// </summary>
    bool IsCancellableAt(DateTimeOffset scheduledAt, DateTimeOffset moment);

    /// <summary>
    /// The later of the grace window and the undo window (ADR-0048): an account is never erased while a
    /// live undo link exists, because following it cancels the deletion (#685).
    /// </summary>
    DateTimeOffset FinalizesAt(DateTimeOffset scheduledAt, DateTimeOffset? revertArmedAt);

    /// <summary>
    /// Read the way <see cref="IsDueBy"/> reads it. From then on the login page says the deletion is under
    /// way (#916).
    /// </summary>
    bool IsDueAt(DateTimeOffset scheduledAt, DateTimeOffset? revertArmedAt, DateTimeOffset moment);

    /// <summary>
    /// The same rule as <see cref="FinalizesAt"/>, in a form the database can run.
    /// </summary>
    Expression<Func<ApplicationUser, bool>> IsDueBy(DateTimeOffset moment);
}
