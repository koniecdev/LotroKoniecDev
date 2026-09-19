using System.Linq.Expressions;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.API.Services.Gdpr;

/// <summary>
/// The two dates a scheduled deletion runs on, and the query that finds the accounts those dates have
/// caught up with (ADR-0031, amended by #685).
/// </summary>
/// <remarks>
/// It exists because the erasure date was worked out in four places — the response header, the
/// e-mail, the login message and the finalizer's query — and #685 gave it a second term. A promise
/// the finalizer does not keep is worse than no promise, so there is one implementation and the
/// query is built from it.
/// </remarks>
internal interface IAccountDeletionSchedule
{
    /// <summary>
    /// When the cancel link stops working. Its token's lifespan is the grace period, measured from the
    /// moment the deletion was scheduled, so this is the grace window and nothing else.
    /// </summary>
    DateTimeOffset CancellableUntil(DateTimeOffset scheduledAt);

    /// <summary>
    /// When the account is really erased: the later of the grace window and the undo window of
    /// ADR-0048. An account is never erased while a live undo link for it exists, because following
    /// that link cancels the deletion (#685).
    /// </summary>
    DateTimeOffset FinalizesAt(DateTimeOffset scheduledAt, DateTimeOffset? revertArmedAt);

    /// <summary>
    /// The same rule as <see cref="FinalizesAt"/>, in a form the database can run: the accounts whose
    /// erasure date is at or before <paramref name="moment"/>.
    /// </summary>
    Expression<Func<ApplicationUser, bool>> IsDueBy(DateTimeOffset moment);
}
