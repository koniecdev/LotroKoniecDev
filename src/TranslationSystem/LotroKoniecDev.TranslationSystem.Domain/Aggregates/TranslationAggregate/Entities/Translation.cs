using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Guards;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Core.Errors;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Constants;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;

/// <summary>
/// One text-fragment row of the translation domain (spec 0001). There is a single mutable row per
/// <see cref="FragmentKey"/>. It holds the English source, the optional Polish text and the version
/// pointers, so rows are not duplicated on every patch.
/// </summary>
public sealed class Translation : AggregateRoot<TranslationId>
{
    public FragmentKey FragmentKey { get; }
    public TranslationSource Source { get; private set; }
    public string? TranslatedText { get; private set; }
    public string? PreviousSourceText { get; private set; }
    public TranslatorId? SubmittedById { get; private set; }
    public TranslatorId? ApprovedById { get; private set; }
    public TranslationStatus Status { get; private set; }
    public GameVersionId IntroducedInVersion { get; private set; }
    public GameVersionId? LastSourceChangeInVersion { get; private set; }
    public GameVersionId? RemovedInVersion { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsRemoved => RemovedInVersion is not null;

    /// <summary>
    /// A row that was just added, in the baseline import or in a diff: English source only, no Polish
    /// yet. Stamps <see cref="IntroducedInVersion"/>.
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
    /// The English changed (spec 0001). The stored source is overwritten and
    /// <see cref="LastSourceChangeInVersion"/> is stamped. If Polish work exists it is parked as
    /// <see cref="TranslationStatus.NeedsReview"/>, and the English it was written against is kept in
    /// <see cref="PreviousSourceText"/> so the translator can compare the two.
    /// A soft removal is cleared here as well: a pair that comes back with a different source lands
    /// here. If the English changes again while the row is still
    /// <see cref="TranslationStatus.NeedsReview"/>, <see cref="PreviousSourceText"/> stays as it is,
    /// so the translator still sees the English their Polish belongs to. It is refreshed only when the
    /// row is drafted again (<see cref="ProvideTranslation"/>).
    /// </summary>
    public void ApplySourceChange(TranslationSource newSource, GameVersionId changedInVersion, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(newSource);
        Ensure.NotEmpty(changedInVersion);

        // Keep the old English only when the current Polish was still valid (Draft or Approved).
        // Coming back from NeedsReview would overwrite it with a source the translator never saw.
        if (Status is TranslationStatus.Draft or TranslationStatus.Approved)
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
    /// Soft removal (spec 0001): the row drops out of translation work and out of the distributed
    /// file, but it is never deleted. <see cref="Restore"/> undoes it when SSG adds the pair back.
    /// </summary>
    public void MarkRemoved(GameVersionId removedInVersion, DateTimeOffset now)
    {
        Ensure.NotEmpty(removedInVersion);

        RemovedInVersion = removedInVersion;
        UpdatedAt = now;
    }

    /// <summary>
    /// The pair came back with the same source (spec 0001), so the soft removal is cleared and the
    /// previous status stays, <see cref="TranslationStatus.Approved"/> included. The old Polish is
    /// still valid.
    /// </summary>
    public void Restore(DateTimeOffset now)
    {
        RemovedInVersion = null;
        UpdatedAt = now;
    }

    /// <summary>
    /// Stores the Polish draft for this row and stamps the translator who sent it (spec 0001, #100).
    /// Any status (Untranslated, Draft, Approved or NeedsReview) becomes
    /// <see cref="TranslationStatus.Draft"/>: editing an approved row takes it out of the distributed
    /// set until someone approves it again. <see cref="PreviousSourceText"/> is left alone, so a
    /// translator reworking an invalidated row still sees the old English until approve clears it.
    /// The text is stored as it is, with its <c>&lt;--DO_NOT_TOUCH!--&gt;</c> placeholders.
    /// Text the DAT cannot hold is refused (#598). Like the blank-text guard next to it, this is a
    /// programmer-error check, not a message for the translator. Turning it into one is the job of
    /// <c>UpsertTranslation.Validator</c>.
    /// </summary>
    public void ProvideTranslation(string translatedText, TranslatorId submittedBy, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(translatedText);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            translatedText.Length, DatFormatConstants.MaxTranslatedTextLength, nameof(translatedText));
        Ensure.NotEmpty(submittedBy);

        TranslatedText = translatedText;
        SubmittedById = submittedBy;
        Status = TranslationStatus.Draft;
        UpdatedAt = now;
    }

    /// <summary>
    /// Approves the Polish for distribution (spec 0001, #101). The row must have Polish text and must
    /// not be removed. The status becomes <see cref="TranslationStatus.Approved"/>, the reviewer is
    /// stamped and <see cref="PreviousSourceText"/> is cleared: once a
    /// <see cref="TranslationStatus.NeedsReview"/> row is translated again and approved, the old
    /// English is no longer needed and the row is distributed again.
    /// </summary>
    public Result Approve(TranslatorId approvedBy, DateTimeOffset now)
    {
        Ensure.NotEmpty(approvedBy);

        if (string.IsNullOrWhiteSpace(TranslatedText))
        {
            return Result.Failure(DomainErrors.TranslationEntity.CannotApproveWithoutTranslation);
        }

        if (IsRemoved)
        {
            return Result.Failure(DomainErrors.TranslationEntity.CannotApproveRemoved);
        }

        Status = TranslationStatus.Approved;
        ApprovedById = approvedBy;
        PreviousSourceText = null;
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
