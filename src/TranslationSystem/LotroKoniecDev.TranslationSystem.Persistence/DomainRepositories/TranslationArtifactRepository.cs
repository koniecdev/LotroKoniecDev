using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationArtifactAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationArtifactAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationArtifactAggregate;
using Microsoft.EntityFrameworkCore;

namespace LotroKoniecDev.TranslationSystem.Persistence.DomainRepositories;

internal sealed class TranslationArtifactRepository
    : GenericRepository<TranslationArtifact, TranslationArtifactId>, ITranslationArtifactRepository
{
    public TranslationArtifactRepository(ApplicationWriteDbContext db) : base(db)
    {
    }

    public async Task<Maybe<TranslationArtifact>> GetByLanguageAsync(string language, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);

        TranslationArtifact? artifact = await DbContext.TranslationArtifacts
            .FirstOrDefaultAsync(translationArtifact => translationArtifact.Language == language, cancellationToken);

        return Maybe<TranslationArtifact>.From(artifact);
    }
}
