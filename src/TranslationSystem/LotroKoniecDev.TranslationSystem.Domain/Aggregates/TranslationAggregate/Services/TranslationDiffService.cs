using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;

/// <summary>
/// Pure diff of an uploaded export against the stored source state, keyed by
/// <see cref="FragmentKey"/> over the full file (spec 0001). Produces a <see cref="TranslationDiffPlan"/>
/// without touching the database — added rows are created here, existing rows are only referenced;
/// the handler realizes the plan inside its transaction after the truncation guard passes.
/// </summary>
public static class TranslationDiffService
{
    public static TranslationDiffPlan ComputePlan(
        IReadOnlyCollection<Translation> existing,
        IReadOnlyCollection<IncomingSourceRow> incoming,
        GameVersionId targetVersion,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(incoming);

        Dictionary<FragmentKey, Translation> existingByKey = existing.ToDictionary(translation => translation.FragmentKey);
        HashSet<FragmentKey> incomingKeys = [.. incoming.Select(row => row.Key)];

        List<Translation> added = [];
        List<TranslationSourceChange> sourceChanges = [];
        List<Translation> restored = [];
        int unchangedCount = 0;
        int invalidatedCount = 0;

        foreach (IncomingSourceRow row in incoming)
        {
            if (!existingByKey.TryGetValue(row.Key, out Translation? current))
            {
                added.Add(Translation.CreateUntranslated(row.Key, row.Source, targetVersion, now).Value);
                continue;
            }

            bool sourceIdentical = current.Source == row.Source;

            if (sourceIdentical)
            {
                if (current.IsRemoved)
                {
                    restored.Add(current);
                }
                else
                {
                    unchangedCount++;
                }

                continue;
            }

            sourceChanges.Add(new TranslationSourceChange(current, row.Source));
            if (HasPolish(current))
            {
                invalidatedCount++;
            }
        }

        List<Translation> removed = [.. existing
            .Where(translation => !translation.IsRemoved && !incomingKeys.Contains(translation.FragmentKey))];

        int comparableExistingCount = existing.Count(translation => !translation.IsRemoved);

        return new TranslationDiffPlan(
            added,
            sourceChanges,
            removed,
            restored,
            unchangedCount,
            invalidatedCount,
            comparableExistingCount);
    }

    private static bool HasPolish(Translation translation)
        => translation.Status is TranslationStatus.Draft or TranslationStatus.Approved or TranslationStatus.NeedsReview;
}
