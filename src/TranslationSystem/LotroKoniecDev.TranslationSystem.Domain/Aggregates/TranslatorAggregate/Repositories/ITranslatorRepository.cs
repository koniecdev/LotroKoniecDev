using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Repositories;

public interface ITranslatorRepository : IRepository<Translator, TranslatorId>
{
    /// <summary>
    /// The key that makes creating a profile on first use safe to repeat (ADR-0004).
    /// </summary>
    Task<Maybe<Translator>> GetByIdentityIdAsync(IdentityId identityId, CancellationToken cancellationToken);

    /// <summary>
    /// When two requests create the same profile at once, the losing insert is detached here, so the
    /// retry does not resend the row the unique index rejected (ADR-0004).
    /// </summary>
    void Detach(Translator translator);
}
