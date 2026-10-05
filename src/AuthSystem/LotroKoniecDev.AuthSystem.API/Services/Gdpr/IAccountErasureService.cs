using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Gdpr;

internal interface IAccountErasureService
{
    /// <summary>
    /// A failure means the account still waits for its erasure, so the finalizer's next run tries again.
    /// When the emergency lock failed too, that is assumed, not known.
    /// It takes no cancellation token, so the stop signal of a shutdown cannot end an erasure halfway.
    /// No later run comes back to an erased account, so a cleanup step that a shutdown skipped would
    /// never be done (#981).
    /// </summary>
    Task<Result<AccountErasureOutcome>> EraseAsync(ApplicationUser user);
}
