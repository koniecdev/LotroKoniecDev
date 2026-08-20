using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Repositories;

public interface ITranslatorRepository : IRepository<Translator, TranslatorId>
{
    /// <summary>
    /// Looks the translator up by the Auth user id. This is the key that makes creating a profile on
    /// first use safe to repeat (ADR-0004).
    /// </summary>
    Task<Maybe<Translator>> GetByIdentityIdAsync(IdentityId identityId, CancellationToken cancellationToken);

    /// <summary>
    /// Drops a translator that was never committed from change tracking. When two requests create the
    /// same profile at once, the losing insert is dropped here (ADR-0004), so the retry does not send
    /// the row the unique index has already rejected.
    /// </summary>
    void Detach(Translator translator);
}
