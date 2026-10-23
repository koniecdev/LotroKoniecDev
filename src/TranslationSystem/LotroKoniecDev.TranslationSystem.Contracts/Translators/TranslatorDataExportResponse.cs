using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

namespace LotroKoniecDev.TranslationSystem.Contracts.Translators;

/// <summary>
/// The caller's TMS-side personal data for the GDPR Art. 15 export (ADR-0032). Row ids only, never the
/// texts: the catalog is not personal data, the link between the user and a row is.
/// <see cref="Profile"/> is <c>null</c> when the identity never got a profile, and then the
/// contribution lists are empty too.
/// </summary>
public sealed record TranslatorDataExportResponse(
    TranslatorProfileExportDto? Profile,
    ContributionSummaryDto Contributions);

public sealed record TranslatorProfileExportDto(
    TranslatorId TranslatorId,
    IdentityId IdentityId,
    string DisplayName,
    string? Email,
    DateTimeOffset ProvisionedAt);

/// <summary>
/// A submitted row is never <see cref="TranslationStatus.Untranslated"/>, so the three submitted
/// counters add up to <see cref="SubmittedTotal"/>.
/// </summary>
public sealed record ContributionSummaryDto(
    int SubmittedTotal,
    int SubmittedDraft,
    int SubmittedApproved,
    int SubmittedNeedsReview,
    int ApprovedTotal,
    IReadOnlyList<ContributionRowDto> SubmittedRows,
    IReadOnlyList<ContributionRowDto> ApprovedRows);

public sealed record ContributionRowDto(
    TranslationId Id,
    int FileId,
    long GossipId,
    TranslationStatus Status);
