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

    public async Task<bool> TryRefreshAsync(
        string language,
        string content,
        string contentHash,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);

        int updatedRowCount = await _dbContext.PrecomputedTranslationFiles
            .Where(precomputedTranslationFile => precomputedTranslationFile.Language == language)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(precomputedTranslationFile => precomputedTranslationFile.Content, content)
                    .SetProperty(precomputedTranslationFile => precomputedTranslationFile.ContentHash, contentHash)
                    .SetProperty(precomputedTranslationFile => precomputedTranslationFile.GeneratedAt, generatedAt),
                cancellationToken);

        return updatedRowCount > 0;
    }

    public void Insert(PrecomputedTranslationFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        _dbContext.PrecomputedTranslationFiles.Add(file);
    }
}
