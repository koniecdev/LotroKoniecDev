using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationArtifactAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationArtifactAggregate;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationArtifactAggregate.Repositories;

public interface ITranslationArtifactRepository : IRepository<TranslationArtifact, TranslationArtifactId>
{
    /// <summary>The artifact is upserted by its natural key — one row per language.</summary>
    Task<Maybe<TranslationArtifact>> GetByLanguageAsync(string language, CancellationToken cancellationToken);
}
