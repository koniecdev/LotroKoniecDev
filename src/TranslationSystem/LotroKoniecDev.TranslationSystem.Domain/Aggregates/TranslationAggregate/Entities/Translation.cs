using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Guards;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Core.Errors;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;

/// <summary>
/// One text-fragment row of the translation domain (spec 0001): a single mutable row per
/// <see cref="FragmentKey"/> carrying the English source, optional Polish content and the
/// version pointers that give per-version grouping without duplicating rows each patch.
/// </summary>
public sealed class Translation : AggregateRoot<TranslationId>
{
    public FragmentKey FragmentKey { get; }
    public TranslationSource Source { get; private set; }
    public string? TranslatedText { get; private set; }
    public string? PreviousSourceText { get; private set; }
    public IdentityId? SubmittedById { get; private set; }
    public TranslationStatus Status { get; private set; }
    public GameVersionId IntroducedInVersion { get; private set; }
    public GameVersionId? LastSourceChangeInVersion { get; private set; }
    public GameVersionId? RemovedInVersion { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsRemoved => RemovedInVersion is not null;

    /// <summary>
    /// An <em>added</em> row (baseline or diff): English source only, no Polish, available for
    /// translation. Stamps <see cref="IntroducedInVersion"/>.
    /// </summary>
    public static Result<Translation> CreateUntranslated(
        FragmentKey fragmentKey,
        TranslationSource source,
        GameVersionId introducedInVersion,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(fragmentKey);
        ArgumentNullException.ThrowIfNull(source);
        Ensure.NotEmpty(introducedInVersion);
        Ensure.NotEmpty(now);

        Translation instance = new(TranslationId.Create(), fragmentKey, source, introducedInVersion, now);

        return Result.Success(instance);
    }

    /// <summary>
    /// Source-changed (spec 0001): overwrite the stored English; if Polish work exists it is
    /// invalidated (parked as <see cref="TranslationStatus.NeedsReview"/>, the superseded English
    /// kept in <see cref="PreviousSourceText"/> for side-by-side context); stamps
    /// <see cref="LastSourceChangeInVersion"/>. Also clears any soft-removal — a re-added pair
    /// whose source differs lands here.
    /// </summary>
    public void ApplySourceChange(TranslationSource newSource, GameVersionId changedInVersion, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(newSource);
        Ensure.NotEmpty(changedInVersion);

        if (Status is TranslationStatus.Draft or TranslationStatus.Approved or TranslationStatus.NeedsReview)
        {
            PreviousSourceText = Source.Text;
            Status = TranslationStatus.NeedsReview;
        }

        Source = newSource;
        LastSourceChangeInVersion = changedInVersion;
        RemovedInVersion = null;
        UpdatedAt = now;
    }

    /// <summary>
    /// Removed (spec 0001): soft-mark — excluded from translation work and the distributed file,
    /// never hard-deleted. Reversible via <see cref="Restore"/> when SSG re-adds the pair.
    /// </summary>
    public void MarkRemoved(GameVersionId removedInVersion, DateTimeOffset now)
    {
        Ensure.NotEmpty(removedInVersion);

        RemovedInVersion = removedInVersion;
        UpdatedAt = now;
    }

    /// <summary>
    /// Re-added with an identical source (spec 0001): clear the soft-removal; the previous status
    /// — including <see cref="TranslationStatus.Approved"/> — stands, because the old Polish is
    /// still valid.
    /// </summary>
    public void Restore(DateTimeOffset now)
    {
        RemovedInVersion = null;
        UpdatedAt = now;
    }

    /// <summary>
    /// Attaches (or replaces) the Polish draft for this row and stamps the submitting translator
    /// (spec 0001, #100). Any prior status — Untranslated, Draft, Approved or NeedsReview — moves to
    /// <see cref="TranslationStatus.Draft"/>: editing an Approved row deliberately pulls it out of the
    /// distributed set until it is re-approved. <see cref="PreviousSourceText"/> is left untouched, so
    /// re-translating an invalidated row keeps the superseded English for side-by-side context until
    /// approve clears it. The text is stored verbatim, preserving its <c>&lt;--DO_NOT_TOUCH!--&gt;</c>
    /// placeholders; the placeholder-count-mismatch warning UX lives in M3.
    /// </summary>
    public void ProvideTranslation(string translatedText, IdentityId submittedBy, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(translatedText);

        TranslatedText = translatedText;
        SubmittedById = submittedBy;
        Status = TranslationStatus.Draft;
        UpdatedAt = now;
    }

    /// <summary>
    /// Approves the Polish draft for distribution (spec 0001). Minimal seed form (#102): requires
    /// Polish content and a non-removed row, then flips the status to
    /// <see cref="TranslationStatus.Approved"/>. #101 enriches it — clears
    /// <see cref="PreviousSourceText"/>, stamps the approver and triggers artifact regeneration.
    /// </summary>
    public Result Approve(DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(TranslatedText))
        {
            return Result.Failure(DomainErrors.TranslationEntity.CannotApproveWithoutTranslation);
        }

        if (IsRemoved)
        {
            return Result.Failure(DomainErrors.TranslationEntity.CannotApproveRemoved);
        }

        Status = TranslationStatus.Approved;
        UpdatedAt = now;

        return Result.Success();
    }

    private Translation(
        TranslationId id,
        FragmentKey fragmentKey,
        TranslationSource source,
        GameVersionId introducedInVersion,
        DateTimeOffset now) : base(id)
    {
        FragmentKey = fragmentKey;
        Source = source;
        IntroducedInVersion = introducedInVersion;
        Status = TranslationStatus.Untranslated;
        CreatedAt = now;
        UpdatedAt = now;
    }

    private Translation()
    {
        FragmentKey = null!;
        Source = null!;
    }
}
