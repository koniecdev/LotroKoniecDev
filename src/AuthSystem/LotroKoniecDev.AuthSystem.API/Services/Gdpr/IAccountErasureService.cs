using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Gdpr;

internal interface IAccountErasureService
{
    /// <summary>
    /// A failure means the account still waits (assumed when the emergency lock failed too), so the next
    /// run tries again. No cancellation token, so a shutdown does not end an erasure halfway: no run comes
    /// back to an erased account, so a skipped cleanup step would never be done (#981). Only a kill can
    /// still cut it; the sign is an anonymized account that still has roles, claims or logins. The cleanup
    /// may empty the context, so <paramref name="user"/> must not be saved afterwards.
    /// </summary>
    Task<Result<AccountErasureOutcome>> EraseAsync(ApplicationUser user);
}
