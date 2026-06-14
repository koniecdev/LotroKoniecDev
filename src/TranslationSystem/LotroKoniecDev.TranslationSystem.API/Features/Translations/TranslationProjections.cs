using System.Linq.Expressions;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.TranslationAggregate;

namespace LotroKoniecDev.TranslationSystem.API.Features.Translations;

/// <summary>
/// Shared read-model projections so the get-one, upsert and approve slices return an identical
/// translation view — including the joined submitter / approver display names (ADR-0004) — without
/// duplicating the projection expression.
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
