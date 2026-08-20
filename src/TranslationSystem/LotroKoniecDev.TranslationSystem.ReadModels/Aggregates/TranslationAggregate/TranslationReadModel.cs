using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.TranslatorAggregate;
using LotroKoniecDev.TranslationSystem.ReadModels.Core.BuildingBlocks;

namespace LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.TranslationAggregate;

public sealed record TranslationReadModel(
    TranslationId Id,
    int FileId,
    long GossipId,
    string SourceText,
    string? ArgsOrder,
    string? ArgsId,
    string? TranslatedText,
    string? PreviousSourceText,
    TranslatorId? SubmittedById,
    TranslatorId? ApprovedById,
    TranslationStatus Status,
    GameVersionId IntroducedInVersion,
    GameVersionId? LastSourceChangeInVersion,
    GameVersionId? RemovedInVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt) : IReadOnlyEntity<TranslationId>
{
    /// <summary>The translator who sent the Polish, joined so the display name can be shown (ADR-0004).</summary>
    public TranslatorReadModel? SubmittedBy { get; init; }

    /// <summary>The reviewer who approved the row, joined so the display name can be shown (ADR-0004).</summary>
    public TranslatorReadModel? ApprovedBy { get; init; }
}
