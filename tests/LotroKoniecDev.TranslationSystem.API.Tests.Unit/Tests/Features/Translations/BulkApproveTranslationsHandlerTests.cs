using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Enums;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;
using LotroKoniecDev.TranslationSystem.API.Features.Translations;
using LotroKoniecDev.TranslationSystem.API.Tests.Unit.Shared;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using NSubstitute;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Features.Translations;

public sealed class BulkApproveTranslationsHandlerTests
{
    private const int FileId = 620756992;
    private static readonly DateTimeOffset Now = new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly GameVersionId VersionId = GameVersionId.Create();
    private static readonly TranslatorId Submitter = TranslatorId.Create();
    private static readonly TranslatorId Approver = TranslatorId.Create();

    // ITranslationRepository / IUnitOfWork are genuine public boundaries (stubbed); the provisioner +
    // rebuild scheduler are internal interfaces NSubstitute can't proxy, so each gets a hand-written
    // double.
    private readonly ITranslationRepository _translationRepository = Substitute.For<ITranslationRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly List<string> _callOrder = [];
    private readonly RecordingScheduler _rebuildScheduler;

    private StubTranslatorProvisioner _provisioner = new(Result.Success(Approver));

    public BulkApproveTranslationsHandlerTests()
    {
        _rebuildScheduler = new RecordingScheduler(_callOrder);
        _unitOfWork.When(unitOfWork => unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()))
            .Do(_ => _callOrder.Add(nameof(IUnitOfWork.SaveChangesAsync)));
    }

    private BulkApproveTranslations.Handler CreateHandler()
        => new(
            new BulkApproveTranslations.Validator(),
            _translationRepository,
            _unitOfWork,
            _provisioner,
            TimeProvider.System,
            _rebuildScheduler);

    private void GivenStoredRows(params Translation[] rows)
        => _translationRepository.GetByIdsAsync(Arg.Any<IReadOnlyList<TranslationId>>(), Arg.Any<CancellationToken>())
            .Returns(rows);

    private static BulkApproveTranslations.Command CommandFor(params TranslationId[] ids)
        => new(ids);

    private static Translation Draft(int gossipId, string polish = "Polski")
    {
        Translation row = Untranslated(gossipId);
        row.ProvideTranslation(polish, Submitter, Now);
        return row;
    }

    private static Translation NeedsReview(int gossipId, string polish = "Stary polski")
    {
        Translation row = Untranslated(gossipId, source: "Old English");
        row.ProvideTranslation(polish, Submitter, Now);
        row.ApplySourceChange(TranslationSource.Create("New English", null, null).Value, VersionId, Now);
        return row;
    }

    private static Translation RemovedDraft(int gossipId, string polish = "Polski")
    {
        Translation row = Draft(gossipId, polish);
        row.MarkRemoved(VersionId, Now);
        return row;
    }

    private static Translation Untranslated(int gossipId, string source = "English")
        => Translation.CreateUntranslated(
            FragmentKey.Create(FileId, gossipId).Value,
            TranslationSource.Create(source, null, null).Value,
            VersionId,
            Now).Value;

    [Fact]
    public async Task Handle_WhenIdsEmpty_ShouldReturnValidationErrorAndNotPersist()
    {
        // Act
        Result<BulkApproveTranslationsResponse> result =
            await CreateHandler().Handle(CommandFor(), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Translations.Validation");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        _rebuildScheduler.ScheduleCount.ShouldBe(0);
    }

    [Fact]
    public async Task Handle_WhenMoreThanOneHundredIds_ShouldReturnValidationError()
    {
        // Arrange: the cap mirrors the list's max page size; a selection can never span more.
        TranslationId[] ids = [.. Enumerable.Range(0, BulkApproveTranslations.MaxIds + 1).Select(_ => TranslationId.Create())];

        // Act
        Result<BulkApproveTranslationsResponse> result =
            await CreateHandler().Handle(CommandFor(ids), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Translations.Validation");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenAnyIdIsEmpty_ShouldReturnValidationError()
    {
        // Act
        Result<BulkApproveTranslationsResponse> result =
            await CreateHandler().Handle(CommandFor(TranslationId.Create(), TranslationId.Empty), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Translations.Validation");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenProvisioningFails_ShouldReturnFailureAndNotPersist()
    {
        // Arrange: a token without a parseable subject must never be attributed to an empty approver;
        // the batch fails whole and nothing is approved or persisted.
        Translation row = Draft(1);
        GivenStoredRows(row);
        _provisioner = new StubTranslatorProvisioner(Result.Failure<TranslatorId>(new Error(
            "Translators.Unauthenticated", "no subject", TypeOfError.Forbidden)));

        // Act
        Result<BulkApproveTranslationsResponse> result =
            await CreateHandler().Handle(CommandFor(row.Id), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Translators.Unauthenticated");
        row.Status.ShouldBe(TranslationStatus.Draft);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        _rebuildScheduler.ScheduleCount.ShouldBe(0);
    }

    [Fact]
    public async Task Handle_WithMixedBatch_ShouldApproveOnlyApprovableRowsSkipTheRestPersistOnceAndRebuildOnce()
    {
        // Arrange: a Draft and a NeedsReview row are approvable; an Untranslated row, a removed Draft
        // and an unknown id (no row returned) are not. Best-effort: approve the two, skip the three.
        Translation draft = Draft(1, "Witaj");
        Translation needsReview = NeedsReview(2, "Stary");
        Translation untranslated = Untranslated(3);
        Translation removed = RemovedDraft(4, "Gamma");
        GivenStoredRows(draft, needsReview, untranslated, removed);
        TranslationId unknownId = TranslationId.Create();

        // Act
        Result<BulkApproveTranslationsResponse> result = await CreateHandler().Handle(
            CommandFor(draft.Id, needsReview.Id, untranslated.Id, removed.Id, unknownId), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Requested.ShouldBe(5);
        result.Value.Approved.ShouldBe(2);
        result.Value.Skipped.ShouldBe(3);
        draft.Status.ShouldBe(TranslationStatus.Approved);
        draft.ApprovedById.ShouldBe(Approver);
        needsReview.Status.ShouldBe(TranslationStatus.Approved);
        needsReview.PreviousSourceText.ShouldBeNull();
        untranslated.Status.ShouldBe(TranslationStatus.Untranslated);
        removed.Status.ShouldBe(TranslationStatus.Draft);
        // One commit and one debounced rebuild for the whole batch, regardless of how many rows changed.
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        _rebuildScheduler.ScheduleCount.ShouldBe(1);
    }

    [Fact]
    public async Task Handle_WhenNoRowIsApprovable_ShouldReturnZeroApprovedAndNeitherPersistNorRebuild()
    {
        // Arrange: an already-Approved row keeps its original approver (it is skipped, not re-stamped),
        // and an untranslated row has no Polish to publish; nothing enters the distributed set.
        Translation approved = Draft(1, "Zatwierdzony");
        approved.Approve(Submitter, Now);
        Translation untranslated = Untranslated(2);
        GivenStoredRows(approved, untranslated);

        // Act
        Result<BulkApproveTranslationsResponse> result =
            await CreateHandler().Handle(CommandFor(approved.Id, untranslated.Id), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Requested.ShouldBe(2);
        result.Value.Approved.ShouldBe(0);
        result.Value.Skipped.ShouldBe(2);
        approved.ApprovedById.ShouldBe(Submitter);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        _rebuildScheduler.ScheduleCount.ShouldBe(0);
    }

    [Fact]
    public async Task Handle_WithDuplicateIds_ShouldCountDistinctOnce()
    {
        // Arrange: a client that repeats an id must not double-count it (Approved + Skipped == Requested).
        Translation draft = Draft(1, "Witaj");
        GivenStoredRows(draft);

        // Act
        Result<BulkApproveTranslationsResponse> result =
            await CreateHandler().Handle(CommandFor(draft.Id, draft.Id), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Requested.ShouldBe(1);
        result.Value.Approved.ShouldBe(1);
        result.Value.Skipped.ShouldBe(0);
    }

    [Fact]
    public async Task Handle_OnSuccess_ShouldScheduleTheRebuildAfterTheCommit()
    {
        // Arrange: ADR-0021 §1: the dirty signal must follow SaveChanges, so a zero-debounce rebuild can
        // never publish a snapshot missing its own trigger. Ordering is invisible in the return value.
        Translation draft = Draft(1, "Witaj");
        GivenStoredRows(draft);

        // Act
        Result<BulkApproveTranslationsResponse> result =
            await CreateHandler().Handle(CommandFor(draft.Id), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        _callOrder.ShouldBe([nameof(IUnitOfWork.SaveChangesAsync), nameof(ITranslationFileRebuildScheduler.Schedule)]);
    }

    private sealed class RecordingScheduler : ITranslationFileRebuildScheduler
    {
        private readonly List<string> _callOrder;

        public RecordingScheduler(List<string> callOrder)
        {
            _callOrder = callOrder;
        }

        public int ScheduleCount { get; private set; }

        public void Schedule(string language)
        {
            ScheduleCount++;
            _callOrder.Add(nameof(ITranslationFileRebuildScheduler.Schedule));
        }
    }
}
