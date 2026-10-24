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
/// One mutable row per <see cref="FragmentKey"/>, with version pointers, so rows are not duplicated on
/// every patch (spec 0001).
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
    /// The English changed (spec 0001). Draft or approved Polish is parked as
    /// <see cref="TranslationStatus.NeedsReview"/>, and the English it was written for is kept in
    /// <see cref="PreviousSourceText"/> so the translator can compare. It also clears a soft removal: a
    /// pair that comes back with a different source lands here.
    /// </summary>
    public void ApplySourceChange(TranslationSource newSource, GameVersionId changedInVersion, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(newSource);
        Ensure.NotEmpty(changedInVersion);

        // From NeedsReview, keep PreviousSourceText: overwriting it would show a source the Polish
        // was never written for.
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
    /// Soft removal (spec 0001): the row leaves translation work and the distributed file but is never
    /// deleted, so <see cref="Restore"/> can undo it when SSG adds the pair back.
    /// </summary>
    public void MarkRemoved(GameVersionId removedInVersion, DateTimeOffset now)
    {
        Ensure.NotEmpty(removedInVersion);

        RemovedInVersion = removedInVersion;
        UpdatedAt = now;
    }

    /// <summary>
    /// The pair came back with the same source (spec 0001), so the old Polish is still valid and the
    /// status stays, <see cref="TranslationStatus.Approved"/> included.
    /// </summary>
    public void Restore(DateTimeOffset now)
    {
        RemovedInVersion = null;
        UpdatedAt = now;
    }

    /// <summary>
    /// Any status becomes <see cref="TranslationStatus.Draft"/>, so editing an approved row takes it out
    /// of the distributed set until it is approved again (spec 0001). <see cref="PreviousSourceText"/>
    /// stays until approve clears it. The length and blank guards are programmer-error checks;
    /// <c>UpsertTranslation.Validator</c> gives the translator the message (#598).
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
    /// Approves the Polish for distribution (spec 0001). <see cref="PreviousSourceText"/> is cleared:
    /// once reworked Polish is approved, the old English is no longer needed.
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
