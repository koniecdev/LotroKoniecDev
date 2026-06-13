using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
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
    Guid? SubmittedById,
    TranslationStatus Status,
    GameVersionId IntroducedInVersion,
    GameVersionId? LastSourceChangeInVersion,
    GameVersionId? RemovedInVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt) : IReadOnlyEntity<TranslationId>;
