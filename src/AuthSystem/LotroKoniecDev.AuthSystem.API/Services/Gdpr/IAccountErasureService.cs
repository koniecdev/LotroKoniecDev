using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Gdpr;

internal interface IAccountErasureService
{
    /// <summary>
    /// A failure means the account still waits for its erasure, so the finalizer's next run tries again.
    /// </summary>
    Task<Result<AccountErasureOutcome>> EraseAsync(ApplicationUser user, CancellationToken cancellationToken);
}
