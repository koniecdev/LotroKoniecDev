using System.Linq.Expressions;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.TranslationAggregate;

namespace LotroKoniecDev.TranslationSystem.API.Features.Translations;

/// <summary>
/// The shared read-model projections, so every translation slice returns the same view, including the
/// joined display names of the submitter and the approver (ADR-0004). The upsert slice reads the
/// committed row back through <see cref="ToDetail"/>. The get-one slice writes the same projection out
/// itself, so it can also carry the soft-removal flag it needs for the links.
/// </summary>
internal static class TranslationProjections
{
    public static readonly Expression<Func<TranslationReadModel, TranslationDetailResponse>> ToDetail =
        translation => new TranslationDetailResponse(
            translation.Id,
            translation.FileId,
            translation.GossipId,
            translation.SourceText,
            translation.ArgsOrder,
            translation.ArgsId,
            translation.TranslatedText,
            translation.PreviousSourceText,
            translation.SubmittedBy == null
                ? null
                : new TranslatorSummaryResponse(translation.SubmittedBy.Id, translation.SubmittedBy.DisplayName),
            translation.ApprovedBy == null
                ? null
                : new TranslatorSummaryResponse(translation.ApprovedBy.Id, translation.ApprovedBy.DisplayName),
            translation.Status,
            translation.CreatedAt,
            translation.UpdatedAt);

    public static readonly Expression<Func<TranslationReadModel, TranslationListItemResponse>> ToListItem =
        translation => new TranslationListItemResponse(
            translation.Id,
            translation.FileId,
            translation.GossipId,
            translation.SourceText,
            translation.TranslatedText,
            translation.Status,
            translation.SubmittedBy == null
                ? null
                : new TranslatorSummaryResponse(translation.SubmittedBy.Id, translation.SubmittedBy.DisplayName),
            translation.UpdatedAt);
}
