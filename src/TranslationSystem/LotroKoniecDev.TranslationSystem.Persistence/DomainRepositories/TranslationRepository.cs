using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
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

    public void InsertRange(IEnumerable<Translation> translations)
    {
        ArgumentNullException.ThrowIfNull(translations);

        DbContext.Set<Translation>().AddRange(translations);
    }
}
