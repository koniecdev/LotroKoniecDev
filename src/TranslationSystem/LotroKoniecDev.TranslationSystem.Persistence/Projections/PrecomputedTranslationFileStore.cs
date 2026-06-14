using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Projections;
using Microsoft.EntityFrameworkCore;

namespace LotroKoniecDev.TranslationSystem.Persistence.Projections;

internal sealed class PrecomputedTranslationFileStore : IPrecomputedTranslationFileStore
{
    private readonly ApplicationWriteDbContext _dbContext;

    public PrecomputedTranslationFileStore(ApplicationWriteDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Maybe<PrecomputedTranslationFile>> GetByLanguageAsync(string language, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);

        PrecomputedTranslationFile? file = await _dbContext.PrecomputedTranslationFiles
            .FirstOrDefaultAsync(precomputedTranslationFile => precomputedTranslationFile.Language == language, cancellationToken);

        return Maybe<PrecomputedTranslationFile>.From(file);
    }

    public void Insert(PrecomputedTranslationFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        _dbContext.PrecomputedTranslationFiles.Add(file);
    }
}
