using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using Microsoft.EntityFrameworkCore;

namespace LotroKoniecDev.TranslationSystem.Persistence.DomainRepositories;

internal sealed class TranslationRepository : GenericRepository<Translation, TranslationId>, ITranslationRepository
{
    public TranslationRepository(ApplicationWriteDbContext db) : base(db)
    {
    }

    public async Task<IReadOnlyList<Translation>> GetAllAsync(CancellationToken cancellationToken)
    {
        List<Translation> translations = await DbContext.Set<Translation>()
            .ToListAsync(cancellationToken);

        return translations;
    }

    public async Task<Maybe<Translation>> GetByFragmentKeyAsync(FragmentKey fragmentKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fragmentKey);

        Translation? translation = await DbContext.Set<Translation>()
            .FirstOrDefaultAsync(
                row => row.FragmentKey.FileId == fragmentKey.FileId && row.FragmentKey.GossipId == fragmentKey.GossipId,
                cancellationToken);

        return Maybe<Translation>.From(translation);
    }

    public void InsertRange(IEnumerable<Translation> translations)
    {
        ArgumentNullException.ThrowIfNull(translations);

        DbContext.Set<Translation>().AddRange(translations);
    }

    public async Task<bool> AnyReferencesGameVersionAsync(GameVersionId gameVersionId, CancellationToken cancellationToken)
    {
        bool referenced = await DbContext.Set<Translation>()
            .AnyAsync(
                translation => translation.IntroducedInVersion == gameVersionId
                    || translation.LastSourceChangeInVersion == gameVersionId
                    || translation.RemovedInVersion == gameVersionId,
                cancellationToken);

        return referenced;
    }
}
