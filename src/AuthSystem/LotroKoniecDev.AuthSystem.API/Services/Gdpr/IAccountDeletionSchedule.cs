using System.Linq.Expressions;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.API.Services.Gdpr;

/// <summary>
/// The two dates a scheduled deletion runs on, whether its cancel link still works, whether its erasure
/// date has come, and the query that finds the accounts those dates have caught up with (ADR-0031,
/// amended by #685, #947 and #916).
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
    /// When the cancel link stops working: the scheduled time plus the grace period, and nothing else.
    /// The token in the link lives one grace period from the moment its e-mail is built, so a token
    /// from a late e-mail outlives this date, and the cancel endpoint refuses it anyway (#947).
    /// </summary>
    DateTimeOffset CancellableUntil(DateTimeOffset scheduledAt);

    /// <summary>
    /// Whether the cancel link still works at <paramref name="moment"/>. The link is already dead at
    /// <see cref="CancellableUntil"/> itself, the same boundary <see cref="IsDueBy"/> uses for the grace
    /// period.
    /// </summary>
    bool IsCancellableAt(DateTimeOffset scheduledAt, DateTimeOffset moment);

    /// <summary>
    /// When the account is really erased: the later of the grace window and the undo window of
    /// ADR-0048. An account is never erased while a live undo link for it exists, because following
    /// that link cancels the deletion (#685).
    /// </summary>
    DateTimeOffset FinalizesAt(DateTimeOffset scheduledAt, DateTimeOffset? revertArmedAt);

    /// <summary>
    /// Whether <see cref="FinalizesAt"/> has arrived at <paramref name="moment"/>, read the way
    /// <see cref="IsDueBy"/> reads it. From then on the account only waits for the daily run, and the
    /// login page says the deletion is under way (#916).
    /// </summary>
    bool IsDueAt(DateTimeOffset scheduledAt, DateTimeOffset? revertArmedAt, DateTimeOffset moment);

    /// <summary>
    /// The same rule as <see cref="FinalizesAt"/>, in a form the database can run: the accounts whose
    /// erasure date is at or before <paramref name="moment"/>.
    /// </summary>
    Expression<Func<ApplicationUser, bool>> IsDueBy(DateTimeOffset moment);
}
