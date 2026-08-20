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
    /// Marking a version that is already processed is allowed and changes nothing (spec 0001).
    /// Only a superseded version is refused, because it was skipped on purpose.
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
    /// When a newer version is processed, all older unprocessed versions are marked here in one go
    /// (spec 0001). Marking a superseded version again is safe. A processed version is refused,
    /// because finished work is never undone.
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
    /// A processed version cannot be deleted. An import ran against it and translations point at it
    /// (spec 0001). Any other version was never imported into, so nothing references it and deleting
    /// it frees its version number again (#624).
    /// The delete handler still checks that no translation points at the version. This aggregate only
    /// owns the status rule.
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
