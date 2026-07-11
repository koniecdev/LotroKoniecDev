using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Gdpr;

internal interface IAccountErasureService
{
    Task<Result> EraseAsync(ApplicationUser user, CancellationToken cancellationToken);
}
