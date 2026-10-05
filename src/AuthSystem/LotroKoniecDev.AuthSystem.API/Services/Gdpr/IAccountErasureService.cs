using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Gdpr;

internal interface IAccountErasureService
{
    /// <summary>
    /// A failure means the account still waits for its erasure, so the finalizer's next run tries again.
    /// When the emergency lock failed too, that is assumed, not known.
    /// It takes no cancellation token, so the stop signal of a shutdown does not end an erasure halfway.
    /// No later run comes back to an erased account, so a cleanup step that a shutdown skipped would
    /// never be done (#981). Only killing the process can still cut an erasure, for example Docker at the
    /// end of its stop grace period. The log may then show the "auth data anonymized" line with no
    /// cleanup line after it. A kill can lose the last log lines too, so the sure sign is an anonymized
    /// account that still has roles, claims or logins.
    /// The cleanup may empty the context, so <paramref name="user"/> must not be saved afterwards.
    /// </summary>
    Task<Result<AccountErasureOutcome>> EraseAsync(ApplicationUser user);
}
