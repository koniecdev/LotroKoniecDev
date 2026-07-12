using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

namespace LotroKoniecDev.TranslationSystem.Contracts.Translators;

/// <summary>
/// The caller's TMS-side personal data for the GDPR Art. 15 export (LEGAL-07, ADR-0032): the
/// lazily provisioned translator profile (ADR-0004) plus the contribution attribution — a
/// per-status summary and the identifiers of every row attributed to the caller, per role.
/// Row identifiers only, never the texts: the catalog content is not the user's personal data,
/// the attribution link is. <see cref="Profile"/> is <c>null</c> when no translator profile was
/// ever provisioned for the identity — the contribution lists are then empty by construction.
/// </summary>
public sealed record TranslatorDataExportResponse(
    TranslatorProfileExportDto? Profile,
    ContributionSummaryDto Contributions);

/// <summary>The translator profile as stored in the TMS context (ADR-0004).</summary>
public sealed record TranslatorProfileExportDto(
    TranslatorId TranslatorId,
    IdentityId IdentityId,
    string DisplayName,
    string? Email,
    DateTimeOffset ProvisionedAt);

/// <summary>
/// Per-status counts over the rows the caller submitted, the count of rows the caller approved,
/// and the identifier list per attribution role. A submitted row is never
/// <see cref="TranslationStatus.Untranslated"/> (submitting Polish is what moves it out), so the
/// three counters partition <see cref="SubmittedTotal"/>.
/// </summary>
public sealed record ContributionSummaryDto(
    int SubmittedTotal,
    int SubmittedDraft,
    int SubmittedApproved,
    int SubmittedNeedsReview,
    int ApprovedTotal,
    IReadOnlyList<ContributionRowDto> SubmittedRows,
    IReadOnlyList<ContributionRowDto> ApprovedRows);

/// <summary>One attributed row: the identifiers that pin it in the catalog, plus its status.</summary>
public sealed record ContributionRowDto(
    TranslationId Id,
    int FileId,
    long GossipId,
    TranslationStatus Status);
