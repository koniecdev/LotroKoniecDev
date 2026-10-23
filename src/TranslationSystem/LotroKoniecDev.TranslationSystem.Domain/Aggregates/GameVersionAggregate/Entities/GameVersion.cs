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

    /// <summary>
    /// Marking a processed version again changes nothing (spec 0001). A superseded version is refused,
    /// because it was skipped on purpose.
    /// </summary>
    public Result MarkAsProcessed()
    {
        if (Status is GameVersionStatus.Superseded)
        {
            return Result.Failure(DomainErrors.GameVersionEntity.SupersededCannotBeProcessed(Id));
        }

        Status = GameVersionStatus.Processed;

        return Result.Success();
    }

    /// <summary>
    /// Called for every older unprocessed version when a newer one is processed (spec 0001). A
    /// processed version is refused, because finished work is never undone.
    /// </summary>
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
    /// A processed version had an import run against it, so translations point at it (spec 0001). Any
    /// other version can be deleted to free its number (#624). The delete handler also checks that no
    /// translation points at it; this aggregate owns only the status rule.
    /// </summary>
    public Result EnsureCanBeDeleted()
    {
        // Written as "these statuses may be deleted", not as "anything except Processed", so a new
        // status is never deletable by accident.
        if (Status is not (GameVersionStatus.Unprocessed or GameVersionStatus.Superseded))
        {
            return Result.Failure(DomainErrors.GameVersionEntity.ProcessedCannotBeDeleted(Id));
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
