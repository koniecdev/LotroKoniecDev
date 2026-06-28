using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Guards;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Core.Errors;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;

public sealed class GameVersion : AggregateRoot<GameVersionId>
{
    public LotroNotationVersion LotroNotationVersion { get; }
    public DateTimeOffset DetectedAt { get; }
    public GameVersionStatus Status { get; private set; }

    // Re-upload to an already processed version is allowed and idempotent (spec 0001,
    // GameVersion lifecycle) — only a superseded version can never be processed.
    /// <summary>
    /// Marks the current game version as processed. A version whose status is
    /// <see cref="GameVersionStatus.Superseded"/> cannot be processed.
    /// </summary>
    /// <returns>
    /// A success <see cref="Result"/>, or a failure when the version is superseded.
    /// </returns>
    public Result MarkAsProcessed()
    {
        if (Status is GameVersionStatus.Superseded)
        {
            return Result.Failure(DomainErrors.GameVersionEntity.SupersededCannotBeProcessed(Id));
        }

        Status = GameVersionStatus.Processed;

        return Result.Success();
    }

    // Stacked unprocessed versions are mass-marked when a newer one is processed (spec 0001),
    // so re-marking an already superseded version is a no-op — processed work is never undone.
    public Result MarkSuperseded()
    {
        if (Status is GameVersionStatus.Processed)
        {
            return Result.Failure(DomainErrors.GameVersionEntity.ProcessedCannotBeSuperseded(Id));
        }

        Status = GameVersionStatus.Superseded;

        return Result.Success();
    }

    /// <summary>
    /// Guards deletion: only an <see cref="GameVersionStatus.Unprocessed"/> version may be removed. A
    /// processed or superseded version has been woven into the update lifecycle (spec 0001) and removing
    /// it would orphan the translations that reference it. The cross-aggregate "no translation references
    /// this version" check stays in the delete handler — the aggregate only owns the status invariant.
    /// </summary>
    public Result EnsureCanBeDeleted()
    {
        if (Status is not GameVersionStatus.Unprocessed)
        {
            return Result.Failure(DomainErrors.GameVersionEntity.OnlyUnprocessedCanBeDeleted(Id));
        }

        return Result.Success();
    }

    public static Result<GameVersion> Create(
        LotroNotationVersion version,
        DateTimeOffset detectedAt)
    {
        ArgumentNullException.ThrowIfNull(version);
        Ensure.NotEmpty(detectedAt);

        GameVersion instance = new(GameVersionId.Create(), version, detectedAt);

        return Result.Success(instance);
    }

    private GameVersion(
        GameVersionId id,
        LotroNotationVersion version,
        DateTimeOffset detectedAt) : base(id)
    {
        LotroNotationVersion = version;
        DetectedAt = detectedAt;
        Status = GameVersionStatus.Unprocessed;
    }

    private GameVersion()
    {
        LotroNotationVersion = null!;
    }
}
