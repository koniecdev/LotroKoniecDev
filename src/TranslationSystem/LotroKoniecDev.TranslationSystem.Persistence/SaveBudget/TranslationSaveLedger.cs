using LotroKoniecDev.TranslationSystem.Persistence.Configurations;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LotroKoniecDev.TranslationSystem.Persistence.SaveBudget;

internal sealed class TranslationSaveLedger : ITranslationSaveLedger
{
    // Raw SQL skips the execution strategy, so RecordAsync runs it through one, like the purge and the
    // count. ON CONFLICT then makes a retry after a lost commit answer harmless.
    private static readonly string InsertSql =
        $"""
         INSERT INTO "{DatabaseSchemas.Translation}"."{TranslationSaveConfiguration.TableName}"
             ("{nameof(TranslationSave.Id)}", "{nameof(TranslationSave.TranslatorId)}", "{nameof(TranslationSave.SavedAt)}", "{nameof(TranslationSave.ReplacedPolish)}")
         VALUES (@id, @translatorId, @savedAt, @replacedPolish)
         ON CONFLICT ("{nameof(TranslationSave.Id)}") DO NOTHING
         """;

    private readonly ApplicationWriteDbContext _dbContext;

    public async Task<TranslationSaveCounts> RecordAsync(
        Guid saveId,
        TranslatorId translatorId,
        bool replacedPolish,
        DateTimeOffset savedAt,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        DateTimeOffset windowStart = savedAt - window;

        await _dbContext.TranslationSaves
            .Where(translationSave => translationSave.SavedAt <= windowStart)
            .ExecuteDeleteAsync(cancellationToken);

        // A parameter object belongs to one command, so each attempt builds its own.
        await _dbContext.Database.CreateExecutionStrategy().ExecuteAsync(
            async retryCancellationToken => await _dbContext.Database.ExecuteSqlRawAsync(
                InsertSql,
                [
                    new NpgsqlParameter("id", saveId),
                    new NpgsqlParameter("translatorId", translatorId.Value),
                    new NpgsqlParameter("savedAt", savedAt),
                    new NpgsqlParameter("replacedPolish", replacedPolish)
                ],
                retryCancellationToken),
            cancellationToken);

        TranslationSaveCounts? counts = await _dbContext.TranslationSaves
            .Where(translationSave => translationSave.TranslatorId == translatorId && translationSave.SavedAt > windowStart)
            .GroupBy(translationSave => translationSave.TranslatorId)
            .Select(group => new TranslationSaveCounts(
                group.Count(),
                group.Count(translationSave => translationSave.ReplacedPolish)))
            .SingleOrDefaultAsync(cancellationToken);

        return counts ?? new TranslationSaveCounts(0, 0);
    }

    public async Task RemoveAsync(Guid saveId, CancellationToken cancellationToken)
    {
        await _dbContext.TranslationSaves
            .Where(translationSave => translationSave.Id == saveId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public TranslationSaveLedger(ApplicationWriteDbContext dbContext)
    {
        _dbContext = dbContext;
    }
}
