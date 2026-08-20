using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Enums;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;
using LotroKoniecDev.TranslationSystem.API.Features.Translations;
using LotroKoniecDev.TranslationSystem.API.Tests.Unit.Shared;
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

public sealed class ApproveTranslationHandlerTests
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

    public ApproveTranslationHandlerTests()
    {
        _rebuildScheduler = new RecordingScheduler(_callOrder);
        _unitOfWork.When(unitOfWork => unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()))
            .Do(_ => _callOrder.Add(nameof(IUnitOfWork.SaveChangesAsync)));
    }

    private ApproveTranslation.Handler CreateHandler()
        => new(
            new ApproveTranslation.Validator(),
            _translationRepository,
            _unitOfWork,
            _provisioner,
            TimeProvider.System,
            _rebuildScheduler);

    private static Translation Untranslated(int gossipId = 1, string source = "English")
        => Translation.CreateUntranslated(
            FragmentKey.Create(FileId, gossipId).Value,
            TranslationSource.Create(source, null, null).Value,
            VersionId,
            Now).Value;

    private void GivenStoredRow(Translation translation)
        => _translationRepository.GetByIdAsync(Arg.Any<TranslationId>(), Arg.Any<CancellationToken>())
            .Returns(Maybe<Translation>.From(translation));

    [Fact]
    public async Task Handle_WhenIdEmpty_ShouldReturnValidationError()
    {
        // Act
        Result result = await CreateHandler().Handle(new ApproveTranslation.Command(TranslationId.Empty), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Translations.Validation");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        _rebuildScheduler.ScheduleCount.ShouldBe(0);
    }

    [Fact]
    public async Task Handle_WhenProvisioningFails_ShouldReturnFailureAndNotPersist()
    {
        // Arrange: defensive guard: a token without a parseable subject must never be attributed to
        // an empty approver; the provisioner surfaces that and the handler must not approve or persist.
        Translation row = Untranslated();
        row.ProvideTranslation("Polski", Submitter, Now);
        GivenStoredRow(row);
        _provisioner = new StubTranslatorProvisioner(Result.Failure<TranslatorId>(new Error(
            "Translators.Unauthenticated", "no subject", TypeOfError.Forbidden)));

        // Act
        Result result = await CreateHandler().Handle(new ApproveTranslation.Command(row.Id), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Translators.Unauthenticated");
        row.Status.ShouldBe(TranslationStatus.Draft);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        _rebuildScheduler.ScheduleCount.ShouldBe(0);
    }

    [Fact]
    public async Task Handle_WhenRowUnknown_ShouldReturnNotFound()
    {
        // Arrange
        _translationRepository.GetByIdAsync(Arg.Any<TranslationId>(), Arg.Any<CancellationToken>())
            .Returns(Maybe<Translation>.None);

        // Act
        Result result = await CreateHandler().Handle(new ApproveTranslation.Command(TranslationId.Create()), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("TranslationEntity.NotFound");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        _rebuildScheduler.ScheduleCount.ShouldBe(0);
    }

    [Fact]
    public async Task Handle_WhenRowHasNoTranslation_ShouldReturnConflictAndNotPersist()
    {
        // Arrange: an untranslated row has no Polish to publish (spec 0001).
        Translation row = Untranslated();
        GivenStoredRow(row);

        // Act
        Result result = await CreateHandler().Handle(new ApproveTranslation.Command(row.Id), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("TranslationEntity.CannotApproveWithoutTranslation");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        _rebuildScheduler.ScheduleCount.ShouldBe(0);
    }

    [Fact]
    public async Task Handle_WhenRowRemoved_ShouldReturnConflictAndNotPersist()
    {
        // Arrange: a soft-removed row is excluded from the distributed file (spec 0001).
        Translation row = Untranslated();
        row.ProvideTranslation("Polski", Submitter, Now);
        row.MarkRemoved(VersionId, Now);
        GivenStoredRow(row);

        // Act
        Result result = await CreateHandler().Handle(new ApproveTranslation.Command(row.Id), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("TranslationEntity.CannotApproveRemoved");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        _rebuildScheduler.ScheduleCount.ShouldBe(0);
    }

    [Fact]
    public async Task Handle_OnDraftRow_ShouldApproveStampApproverPersistAndRebuild()
    {
        // Arrange
        Translation row = Untranslated();
        row.ProvideTranslation("Witaj", Submitter, Now);
        GivenStoredRow(row);

        // Act
        Result result = await CreateHandler().Handle(new ApproveTranslation.Command(row.Id), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        row.Status.ShouldBe(TranslationStatus.Approved);
        row.ApprovedById.ShouldBe(Approver);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        // The row enters the distributed set, so a rebuild is always scheduled on a successful approve.
        _rebuildScheduler.ScheduleCount.ShouldBe(1);
    }

    [Fact]
    public async Task Handle_OnSuccess_ShouldScheduleTheRebuildAfterTheCommit()
    {
        // Arrange: ADR-0021 §1: the dirty signal must follow SaveChanges; signalled before the
        // commit, a zero-debounce rebuild could publish a snapshot missing its own trigger and park
        // the artifact stale. Ordering is invisible in the return value, hence the call log.
        Translation row = Untranslated();
        row.ProvideTranslation("Witaj", Submitter, Now);
        GivenStoredRow(row);

        // Act
        Result result = await CreateHandler().Handle(new ApproveTranslation.Command(row.Id), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        _callOrder.ShouldBe([nameof(IUnitOfWork.SaveChangesAsync), nameof(ITranslationFileRebuildScheduler.Schedule)]);
    }

    [Fact]
    public async Task Handle_OnNeedsReviewRow_ShouldClearInvalidationAndRebuild()
    {
        // Arrange: a re-translated invalidated row: approving resolves the invalidation (spec 0001).
        Translation row = Untranslated(source: "Old English");
        row.ProvideTranslation("Stary polski", Submitter, Now);
        row.ApplySourceChange(TranslationSource.Create("New English", null, null).Value, VersionId, Now);
        row.Status.ShouldBe(TranslationStatus.NeedsReview);
        GivenStoredRow(row);

        // Act
        Result result = await CreateHandler().Handle(new ApproveTranslation.Command(row.Id), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        row.Status.ShouldBe(TranslationStatus.Approved);
        row.PreviousSourceText.ShouldBeNull();
        _rebuildScheduler.ScheduleCount.ShouldBe(1);
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
