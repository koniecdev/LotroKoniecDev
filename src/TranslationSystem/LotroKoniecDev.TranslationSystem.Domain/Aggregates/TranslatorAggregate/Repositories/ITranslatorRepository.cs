using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Repositories;

public interface ITranslatorRepository : IRepository<Translator, TranslatorId>
{
    /// <summary>
    /// Looks up the translator by the cross-context Auth user id — the key lazy provisioning is
    /// idempotent on (ADR-0004).
    /// </summary>
    Task<Maybe<Translator>> GetByIdentityIdAsync(IdentityId identityId, CancellationToken cancellationToken);

    /// <summary>
    /// Drops a not-yet-committed translator from change tracking. Used to discard the losing insert
    /// after a concurrent first-write race (ADR-0004) so neither the retry nor the caller's shared
    /// unit of work re-attempts the row the unique index already rejected.
    /// </summary>
    void Detach(Translator translator);
}
