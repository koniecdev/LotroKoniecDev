using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

namespace LotroKoniecDev.TranslationSystem.Contracts.Translators;

/// <summary>
/// The caller's TMS-side personal data for the GDPR Art. 15 export (LEGAL-07, ADR-0032): their
/// translator profile (ADR-0004) and what they contributed, as a summary per status plus the ids of
/// every row credited to them, per role.
/// It carries row ids only and never the texts. The catalog content is not the user's personal data;
/// the link between them and a row is. <see cref="Profile"/> is <c>null</c> when the identity never
/// got a translator profile, and then the contribution lists are empty as well.
/// </summary>
public sealed record TranslatorDataExportResponse(
    TranslatorProfileExportDto? Profile,
    ContributionSummaryDto Contributions);

/// <summary>The translator profile as the TMS stores it (ADR-0004).</summary>
public sealed record TranslatorProfileExportDto(
    TranslatorId TranslatorId,
    IdentityId IdentityId,
    string DisplayName,
    string? Email,
    DateTimeOffset ProvisionedAt);

/// <summary>
/// Counts per status over the rows the caller submitted, the number of rows they approved, and the
/// list of ids for each role. A submitted row is never
/// <see cref="TranslationStatus.Untranslated"/>, because sending Polish is what moves it out of that
/// status, so the three counters add up to <see cref="SubmittedTotal"/>.
/// </summary>
public sealed record ContributionSummaryDto(
    int SubmittedTotal,
    int SubmittedDraft,
    int SubmittedApproved,
    int SubmittedNeedsReview,
    int ApprovedTotal,
    IReadOnlyList<ContributionRowDto> SubmittedRows,
    IReadOnlyList<ContributionRowDto> ApprovedRows);

/// <summary>One credited row: the ids that locate it in the catalog, plus its status.</summary>
public sealed record ContributionRowDto(
    TranslationId Id,
    int FileId,
    long GossipId,
    TranslationStatus Status);
