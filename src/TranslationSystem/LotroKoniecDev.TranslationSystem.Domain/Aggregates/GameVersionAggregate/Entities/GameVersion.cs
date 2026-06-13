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

    public static Result<GameVersion> Create(LotroNotationVersion version, DateTimeOffset detectedAt)
    {
        ArgumentNullException.ThrowIfNull(version);
        Ensure.NotEmpty(detectedAt);

        GameVersion instance = new(GameVersionId.Create(), version, detectedAt);

        return Result.Success(instance);
    }

    // Re-upload to an already processed version is allowed and idempotent (spec 0001,
    // GameVersion lifecycle) — only a superseded version can never be processed.
    public Result MarkProcessed()
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
