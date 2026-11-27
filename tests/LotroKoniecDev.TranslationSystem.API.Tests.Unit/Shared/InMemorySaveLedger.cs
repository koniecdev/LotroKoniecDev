using LotroKoniecDev.TranslationSystem.Persistence.SaveBudget;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Shared;

/// <summary>
/// Counts like the real ledger: the permits just recorded are included.
/// </summary>
internal sealed class InMemorySaveLedger : ITranslationSaveLedger
{
    private readonly List<(Guid Id, TranslatorId TranslatorId, bool ReplacedPolish)> _saves = [];

    public int RecordCalls { get; private set; }

    /// <summary>
    /// Cancels the request right after the permits are recorded, as a client that leaves would.
    /// </summary>
    public CancellationTokenSource? CancelAfterRecord { get; set; }

    /// <summary>
    /// Makes every hand-back fail, as a database that is down would.
    /// </summary>
    public Exception? RemoveFailure { get; set; }

    /// <summary>
    /// Stores the permits, then fails, as a count that breaks after the insert committed would.
    /// </summary>
    public Exception? RecordFailure { get; set; }

    public void Seed(TranslatorId translatorId, int saves, int overwrites)
    {
        for (int index = 0; index < saves; index++)
        {
            _saves.Add((Guid.NewGuid(), translatorId, index < overwrites));
        }
    }

    public TranslationSaveCounts CountFor(TranslatorId translatorId)
        => new(
            _saves.Count(save => save.TranslatorId == translatorId),
            _saves.Count(save => save.TranslatorId == translatorId && save.ReplacedPolish));

    public Task<TranslationSaveCounts> RecordAsync(
        IReadOnlyList<TranslationSavePermit> permits,
        TranslatorId translatorId,
        DateTimeOffset savedAt,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        RecordCalls++;
        _saves.AddRange(permits.Select(permit => (permit.Id, translatorId, permit.ReplacedPolish)));
        CancelAfterRecord?.Cancel();
        return RecordFailure is null
            ? Task.FromResult(CountFor(translatorId))
            : Task.FromException<TranslationSaveCounts>(RecordFailure);
    }

    public Task DeleteExpiredAsync(DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task RemoveAsync(IReadOnlyList<Guid> saveIds, CancellationToken cancellationToken)
    {
        // Like EF Core, a cancelled token stops the delete.
        cancellationToken.ThrowIfCancellationRequested();
        if (RemoveFailure is not null)
        {
            throw RemoveFailure;
        }

        _saves.RemoveAll(save => saveIds.Contains(save.Id));
        return Task.CompletedTask;
    }
}
