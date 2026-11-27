using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Enums;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.Tests.Shared;
using LotroKoniecDev.TranslationSystem.API;
using LotroKoniecDev.TranslationSystem.API.Auth.Provisioning;
using LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;
using LotroKoniecDev.TranslationSystem.API.Features.Translations;
using LotroKoniecDev.TranslationSystem.API.Tests.Unit.Shared;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;
using LotroKoniecDev.TranslationSystem.Persistence.SaveBudget;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Constants;
using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.TranslatorAggregate;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Features.Translations;

public sealed class UpsertTranslationHandlerTests
{
    private const int FileId = 620756992;
    private const string SubmitterName = "Aragorn";
    private static readonly DateTimeOffset Now = new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly GameVersionId VersionId = GameVersionId.Create();
    private static readonly TranslatorId CurrentTranslator = TranslatorId.Create();

    // The provisioner and the rebuild scheduler are internal, which NSubstitute cannot proxy, so each
    // gets a hand-written double.
    private readonly ITranslationRepository _translationRepository = Substitute.For<ITranslationRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly List<TranslationReadModel> _readModels = [];
    private readonly List<string> _callOrder = [];

    private readonly RecordingRebuildScheduler _rebuildScheduler;
    private readonly InMemorySaveLedger _saveLedger = new();
    private readonly CapturingLoggerFactory _loggerFactory = new();

    private StubTranslatorProvisioner _provisioner = new(Result.Success(CurrentTranslator));
    private TranslationSaveBudgetSettings _budgetSettings = new();
    private bool _callerIsAdmin;

    public UpsertTranslationHandlerTests()
    {
        _rebuildScheduler = new RecordingRebuildScheduler(_callOrder);
        _unitOfWork.When(unitOfWork => unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()))
            .Do(_ => _callOrder.Add(nameof(IUnitOfWork.SaveChangesAsync)));
    }

    [Fact]
    public async Task Handle_WhenTranslatedTextEmpty_ShouldReturnValidationError()
    {
        // Act
        Result<TranslationDetailResponse> result = await CreateHandler().Handle(Command(1, "   "), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Translations.Validation");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenTranslatedTextLongerThanTheDatAllows_ShouldReturnValidationError()
    {
        // Arrange: the patcher could never write this row (#598).
        GivenStoredRow(Untranslated());
        string tooLong = new('ż', DatFormatConstants.MaxTranslatedTextLength + 1);

        // Act
        Result<TranslationDetailResponse> result = await CreateHandler().Handle(Command(1, tooLong), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Translations.Validation");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenTranslatedTextExactlyAtTheDatLimit_ShouldPersist()
    {
        // Arrange: the boundary itself is legal.
        Translation row = Untranslated();
        GivenStoredRow(row);
        string atLimit = new('ż', DatFormatConstants.MaxTranslatedTextLength);
        GivenReadBack(row, atLimit);

        // Act
        Result<TranslationDetailResponse> result = await CreateHandler().Handle(Command(1, atLimit), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        row.TranslatedText.ShouldBe(atLimit);
    }

    [Fact]
    public async Task Handle_WhenProvisioningFails_ShouldReturnFailureAndNotPersist()
    {
        // Arrange: a token without a usable subject must never be attributed.
        Translation row = Untranslated();
        GivenStoredRow(row);
        _provisioner = new StubTranslatorProvisioner(Result.Failure<TranslatorId>(new Error(
            "Translators.Unauthenticated", "no subject", TypeOfError.Forbidden)));

        // Act
        Result<TranslationDetailResponse> result = await CreateHandler().Handle(Command(1, "Polski"), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Translators.Unauthenticated");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenRowUnknown_ShouldReturnNotFound()
    {
        // Arrange
        _translationRepository.GetByFragmentKeyAsync(Arg.Any<FragmentKey>(), Arg.Any<CancellationToken>())
            .Returns(Maybe<Translation>.None);

        // Act
        Result<TranslationDetailResponse> result = await CreateHandler().Handle(Command(404, "Polski"), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("TranslationEntity.NotFound");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenRowRemoved_ShouldReturnConflictAndNotPersist()
    {
        // Arrange: a soft-removed row is excluded from translation work (spec 0001).
        Translation removed = Untranslated();
        removed.ProvideTranslation("Polski", CurrentTranslator, Now);
        removed.MarkRemoved(VersionId, Now);
        GivenStoredRow(removed);

        // Act
        Result<TranslationDetailResponse> result = await CreateHandler().Handle(Command(1, "Nowy polski"), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("TranslationEntity.CannotEditRemoved");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_OnUntranslatedRow_ShouldSetDraftStampSubmitterAndNotRebuild()
    {
        // Arrange
        Translation row = Untranslated();
        GivenStoredRow(row);
        GivenReadBack(row, "Witaj");

        // Act
        Result<TranslationDetailResponse> result = await CreateHandler().Handle(Command(1, "Witaj"), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Status.ShouldBe(TranslationStatus.Draft);
        result.Value.TranslatedText.ShouldBe("Witaj");
        result.Value.Submitter.ShouldNotBeNull();
        result.Value.Submitter.Id.ShouldBe(CurrentTranslator);
        result.Value.Submitter.DisplayName.ShouldBe(SubmitterName);
        row.SubmittedById.ShouldBe(CurrentTranslator);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        _rebuildScheduler.ScheduleCount.ShouldBe(0);
    }

    [Fact]
    public async Task Handle_OnApprovedRow_ShouldMoveToDraftAndRebuildArtifact()
    {
        // Arrange: an Approved row is in the distributed file; editing pulls it out (spec 0001 Q1).
        Translation approved = Untranslated();
        approved.ProvideTranslation("Stary polski", CurrentTranslator, Now);
        approved.Approve(CurrentTranslator, Now);
        GivenStoredRow(approved);
        GivenReadBack(approved, "Nowy polski");

        // Act
        Result<TranslationDetailResponse> result = await CreateHandler().Handle(Command(1, "Nowy polski"), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Status.ShouldBe(TranslationStatus.Draft);
        _rebuildScheduler.ScheduleCount.ShouldBe(1);
    }

    [Fact]
    public async Task Handle_OnApprovedRow_ShouldScheduleTheRebuildAfterTheCommit()
    {
        // Arrange: signalled before the commit, a zero-debounce rebuild could miss its own trigger and
        // leave the artifact stale (ADR-0021 §1). The return value cannot show order, hence the call log.
        Translation approved = Untranslated();
        approved.ProvideTranslation("Stary polski", CurrentTranslator, Now);
        approved.Approve(CurrentTranslator, Now);
        GivenStoredRow(approved);
        GivenReadBack(approved, "Nowy polski");

        // Act
        Result<TranslationDetailResponse> result = await CreateHandler().Handle(Command(1, "Nowy polski"), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        _callOrder.ShouldBe([nameof(IUnitOfWork.SaveChangesAsync), nameof(ITranslationFileRebuildScheduler.Schedule)]);
    }

    [Fact]
    public async Task Handle_OnNeedsReviewRow_ShouldMoveToDraftAndKeepPreviousSource()
    {
        // Arrange
        Translation needsReview = Untranslated(source: "Old English");
        needsReview.ProvideTranslation("Stary polski", CurrentTranslator, Now);
        needsReview.ApplySourceChange(TranslationSource.Create("New English", null, null).Value, VersionId, Now);
        GivenStoredRow(needsReview);
        GivenReadBack(needsReview, "Nowy polski");

        // Act
        Result<TranslationDetailResponse> result = await CreateHandler().Handle(Command(1, "Nowy polski"), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Status.ShouldBe(TranslationStatus.Draft);
        result.Value.PreviousSourceText.ShouldBe("Old English");
        result.Value.TranslatedText.ShouldBe("Nowy polski");
        _rebuildScheduler.ScheduleCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("Hail <--DO_NOT_TOUCH!-->!", "Witaj!")]
    [InlineData("Hail <--DO_NOT_TOUCH!-->!", "Witaj <--DO_NOT_TOUCH!--> <--DO_NOT_TOUCH!-->!")]
    [InlineData("Hail!", "Witaj <--DO_NOT_TOUCH!-->!")]
    [InlineData("Hail!", "Witaj <--DO_NOT_TOUCH!--><--DO_NOT_TOUCH!-->!")]
    public async Task Handle_WhenThePlaceholderCountDiffersFromTheEnglish_ShouldRefuseBeforeThePermit(
        string english, string polish)
    {
        // Arrange: the permit commits on its own, so a later refusal would spend it on a save that
        // never happened (#1437).
        Translation row = Untranslated(source: english);
        GivenStoredRow(row);

        // Act
        Result<TranslationDetailResponse> result = await CreateHandler().Handle(Command(1, polish), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("TranslationEntity.TranslatedText.PlaceholderCountMismatch");
        result.Error.Type.ShouldBe(TypeOfError.Validation);
        row.TranslatedText.ShouldBeNull();
        row.Status.ShouldBe(TranslationStatus.Untranslated);
        _saveLedger.RecordCalls.ShouldBe(0);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenThePlaceholderCountMatchesTheEnglish_ShouldPersist()
    {
        // Arrange
        Translation row = Untranslated(source: "Hail <--DO_NOT_TOUCH!-->, friend of <--DO_NOT_TOUCH!-->!");
        GivenStoredRow(row);
        const string polish = "Witaj, <--DO_NOT_TOUCH!-->, przyjacielu <--DO_NOT_TOUCH!-->!";
        GivenReadBack(row, polish);

        // Act
        Result<TranslationDetailResponse> result = await CreateHandler().Handle(Command(1, polish), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        row.TranslatedText.ShouldBe(polish);
    }

    [Fact]
    public async Task Handle_OnNeedsReviewRow_ShouldCheckThePlaceholdersAgainstTheCurrentEnglish()
    {
        // Arrange: the old English had one placeholder and the new one has none.
        Translation needsReview = Untranslated(source: "Hail <--DO_NOT_TOUCH!-->!");
        needsReview.ProvideTranslation("Witaj <--DO_NOT_TOUCH!-->!", CurrentTranslator, Now);
        needsReview.ApplySourceChange(TranslationSource.Create("Hail!", null, null).Value, VersionId, Now);
        GivenStoredRow(needsReview);
        GivenReadBack(needsReview, "Witaj!");

        // Act
        Result<TranslationDetailResponse> result = await CreateHandler().Handle(Command(1, "Witaj!"), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        needsReview.TranslatedText.ShouldBe("Witaj!");
    }

    [Fact]
    public async Task Handle_OnNeedsReviewRow_WhenThePolishFitsOnlyThePreviousEnglish_ShouldRefuse()
    {
        // Arrange: the file ships against the current English, so the previous one never counts.
        Translation needsReview = Untranslated(source: "Hail <--DO_NOT_TOUCH!-->!");
        needsReview.ProvideTranslation("Witaj <--DO_NOT_TOUCH!-->!", CurrentTranslator, Now);
        needsReview.ApplySourceChange(TranslationSource.Create("Hail!", null, null).Value, VersionId, Now);
        GivenStoredRow(needsReview);

        // Act
        Result<TranslationDetailResponse> result = await CreateHandler().Handle(
            Command(1, "Witaj ponownie <--DO_NOT_TOUCH!-->!"), CancellationToken.None);

        // Assert
        result.Error.Code.ShouldBe("TranslationEntity.TranslatedText.PlaceholderCountMismatch");
        needsReview.TranslatedText.ShouldBe("Witaj <--DO_NOT_TOUCH!-->!");
        needsReview.Status.ShouldBe(TranslationStatus.NeedsReview);
    }

    [Fact]
    public async Task Handle_WhenTheSaveBudgetIsSpent_ShouldReturn429CodeAndNotPersist()
    {
        // Arrange
        _budgetSettings = SmallBudget(saveLimit: 2, overwriteLimit: 2);
        _saveLedger.Seed(CurrentTranslator, saves: 2, overwrites: 0);
        Translation row = Untranslated();
        GivenStoredRow(row);

        // Act
        Result<TranslationDetailResponse> result = await CreateHandler().Handle(Command(1, "Witaj"), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Translations.SaveBudgetExhausted");
        result.Error.Type.ShouldBe(TypeOfError.TooManyRequests);
        row.TranslatedText.ShouldBeNull();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenTheSaveBudgetIsSpent_ShouldAnswerWithTheSavesOwnMessage()
    {
        // Arrange: the fill's message names a number of rows; a single save keeps its own (#1587).
        _budgetSettings = SmallBudget(saveLimit: 2, overwriteLimit: 2);
        _saveLedger.Seed(CurrentTranslator, saves: 2, overwrites: 0);
        GivenStoredRow(Untranslated());

        // Act
        Result<TranslationDetailResponse> result = await CreateHandler().Handle(Command(1, "Witaj"), CancellationToken.None);

        // Assert
        result.Error.Message.ShouldBe(TranslationSaveBudgetErrors.SaveBudgetExhausted(2, 24).Message);
    }

    [Fact]
    public async Task Handle_WhenRefused_ShouldHandThePermitBack()
    {
        // Arrange: a refused attempt spends nothing, so the account stays exactly at its limit.
        _budgetSettings = SmallBudget(saveLimit: 2, overwriteLimit: 2);
        _saveLedger.Seed(CurrentTranslator, saves: 2, overwrites: 0);
        GivenStoredRow(Untranslated());

        // Act
        await CreateHandler().Handle(Command(1, "Witaj"), CancellationToken.None);

        // Assert
        _saveLedger.CountFor(CurrentTranslator).ShouldBe(new TranslationSaveCounts(2, 0));
    }

    [Theory]
    [InlineData(TranslationSeed.Draft)]
    [InlineData(TranslationSeed.Approved)]
    public async Task Handle_WhenTheOverwriteBudgetIsSpent_ShouldRefuseARowThatHasPolish(TranslationSeed seed)
    {
        // Arrange
        _budgetSettings = SmallBudget(saveLimit: 10, overwriteLimit: 1);
        _saveLedger.Seed(CurrentTranslator, saves: 1, overwrites: 1);
        Translation row = Translated(seed);
        GivenStoredRow(row);

        // Act
        Result<TranslationDetailResponse> result = await CreateHandler().Handle(Command(1, "Nowy polski"), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Translations.OverwriteBudgetExhausted");
        row.TranslatedText.ShouldBe("Stary polski");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenOnlyTheOverwriteBudgetIsSpent_ShouldStillSaveAnUntranslatedRow()
    {
        // Arrange
        _budgetSettings = SmallBudget(saveLimit: 10, overwriteLimit: 1);
        _saveLedger.Seed(CurrentTranslator, saves: 1, overwrites: 1);
        Translation row = Untranslated();
        GivenStoredRow(row);
        GivenReadBack(row, "Witaj");

        // Act
        Result<TranslationDetailResponse> result = await CreateHandler().Handle(Command(1, "Witaj"), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        row.TranslatedText.ShouldBe("Witaj");
    }

    [Fact]
    public async Task Handle_WhenBothBudgetsAreSpent_ShouldAnswerWithTheSaveRefusalAndOneWarning()
    {
        // Arrange: the overwrite answer would tell the translator that untranslated rows still go
        // through, which is false here.
        _budgetSettings = SmallBudget(saveLimit: 1, overwriteLimit: 1);
        _saveLedger.Seed(CurrentTranslator, saves: 1, overwrites: 1);
        GivenStoredRow(Translated(TranslationSeed.Draft));

        // Act
        Result<TranslationDetailResponse> result = await CreateHandler().Handle(Command(1, "Nowy polski"), CancellationToken.None);

        // Assert
        result.Error.Code.ShouldBe("Translations.SaveBudgetExhausted");
        CapturingLoggerFactory.LogEntry warning = BudgetEntries().ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldContain("budget Saves, outcome Refused");
    }

    [Fact]
    public async Task Handle_WhenTheCallerIsAnAdmin_ShouldNeitherSpendNorBeRefused()
    {
        // Arrange: admins already approve, import and delete versions, so a cap protects nothing.
        _callerIsAdmin = true;
        _budgetSettings = SmallBudget(saveLimit: 1, overwriteLimit: 1);
        _saveLedger.Seed(CurrentTranslator, saves: 5, overwrites: 5);
        Translation row = Translated(TranslationSeed.Approved);
        GivenStoredRow(row);
        GivenReadBack(row, "Nowy polski");

        // Act
        Result<TranslationDetailResponse> result = await CreateHandler().Handle(Command(1, "Nowy polski"), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        _saveLedger.RecordCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Handle_WhenASaveReplacesPolish_ShouldLogTheOverwriteAtInformationAfterTheCommit()
    {
        // Arrange
        Translation row = Translated(TranslationSeed.Approved);
        GivenStoredRow(row);
        GivenReadBack(row, "Nowy polski");
        _unitOfWork.When(unitOfWork => unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()))
            .Do(_ => _callOrder.Add($"log entries at commit: {OverwriteEntries().Count}"));

        // Act
        await CreateHandler().Handle(Command(1, "Nowy polski"), CancellationToken.None);

        // Assert
        CapturingLoggerFactory.LogEntry entry = OverwriteEntries().ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Information);
        entry.Message.ShouldStartWith(TranslationSaveBudgetLogText.OverwrittenPrefix);
        entry.Message.ShouldContain(CurrentTranslator.Value.ToString());
        entry.Message.ShouldContain("previous status Approved, budgeted True");
        _callOrder.ShouldContain("log entries at commit: 0");
    }

    [Fact]
    public async Task Handle_WhenAnAdminReplacesPolish_ShouldLogTheOverwriteAsNotBudgeted()
    {
        // Arrange: the owner's total alert counts every account, admins included.
        _callerIsAdmin = true;
        Translation row = Translated(TranslationSeed.Draft);
        GivenStoredRow(row);
        GivenReadBack(row, "Nowy polski");

        // Act
        await CreateHandler().Handle(Command(1, "Nowy polski"), CancellationToken.None);

        // Assert
        OverwriteEntries().ShouldHaveSingleItem().Message.ShouldContain("budgeted False");
    }

    [Fact]
    public async Task Handle_OnAnUntranslatedRow_ShouldLogNoOverwrite()
    {
        // Arrange
        Translation row = Untranslated();
        GivenStoredRow(row);
        GivenReadBack(row, "Witaj");

        // Act
        await CreateHandler().Handle(Command(1, "Witaj"), CancellationToken.None);

        // Assert
        OverwriteEntries().ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_WhenTheOverwriteIsRefused_ShouldLogNoOverwrite()
    {
        // Arrange: a refused save changed no text, so it must not count toward the total alert.
        _budgetSettings = SmallBudget(saveLimit: 10, overwriteLimit: 1);
        _saveLedger.Seed(CurrentTranslator, saves: 1, overwrites: 1);
        GivenStoredRow(Translated(TranslationSeed.Draft));

        // Act
        await CreateHandler().Handle(Command(1, "Nowy polski"), CancellationToken.None);

        // Assert
        OverwriteEntries().ShouldBeEmpty();
        BudgetEntries().ShouldHaveSingleItem().Message.ShouldContain("budget Overwrites, outcome Refused");
    }

    [Fact]
    public async Task Handle_WhenTheClientLeavesAfterThePermitIsTaken_ShouldStillHandTheRefusedPermitBack()
    {
        // Arrange: the request is cancelled right after the permit is recorded.
        _budgetSettings = SmallBudget(saveLimit: 2, overwriteLimit: 2);
        _saveLedger.Seed(CurrentTranslator, saves: 2, overwrites: 0);
        GivenStoredRow(Untranslated());
        using CancellationTokenSource request = new();
        _saveLedger.CancelAfterRecord = request;

        // Act
        Result<TranslationDetailResponse> result = await CreateHandler().Handle(Command(1, "Witaj"), request.Token);

        // Assert
        result.Error.Code.ShouldBe("Translations.SaveBudgetExhausted");
        _saveLedger.CountFor(CurrentTranslator).ShouldBe(new TranslationSaveCounts(2, 0));
    }

    [Theory]
    [InlineData(RejectedBeforeTheSave.InvalidText)]
    [InlineData(RejectedBeforeTheSave.UnknownRow)]
    [InlineData(RejectedBeforeTheSave.RemovedRow)]
    [InlineData(RejectedBeforeTheSave.PlaceholderMismatch)]
    public async Task Handle_WhenTheRequestIsRejectedBeforeTheSave_ShouldSpendNoPermit(RejectedBeforeTheSave reason)
    {
        // Arrange
        string text = "Polski";
        switch (reason)
        {
            case RejectedBeforeTheSave.InvalidText:
                GivenStoredRow(Untranslated());
                text = "   ";
                break;
            case RejectedBeforeTheSave.UnknownRow:
                _translationRepository.GetByFragmentKeyAsync(Arg.Any<FragmentKey>(), Arg.Any<CancellationToken>())
                    .Returns(Maybe<Translation>.None);
                break;
            case RejectedBeforeTheSave.RemovedRow:
                Translation removed = Translated(TranslationSeed.Draft);
                removed.MarkRemoved(VersionId, Now);
                GivenStoredRow(removed);
                break;
            case RejectedBeforeTheSave.PlaceholderMismatch:
                GivenStoredRow(Untranslated());
                text = "Polski <--DO_NOT_TOUCH!-->";
                break;
        }

        // Act
        Result<TranslationDetailResponse> result = await CreateHandler().Handle(Command(1, text), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        _saveLedger.RecordCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Handle_WhenTheSaveFailsAfterThePermit_ShouldKeepThePermitAndLogNoOverwrite()
    {
        // Arrange: a failed save changed no text, and handing the permit back could not run when the
        // database is what failed (ADR-0072 §4).
        GivenStoredRow(Translated(TranslationSeed.Draft));
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<int>(new InvalidOperationException("database down")));

        // Act
        await Should.ThrowAsync<InvalidOperationException>(
            async () => await CreateHandler().Handle(Command(1, "Nowy polski"), CancellationToken.None));

        // Assert
        _saveLedger.CountFor(CurrentTranslator).ShouldBe(new TranslationSaveCounts(1, 1));
        OverwriteEntries().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Handle_AtOrAboveTheSaveAlertThreshold_ShouldLogOneWarningPerSave(int earlierSaves)
    {
        // Arrange: threshold 3, so the 3rd save is the first to warn and the 4th warns again.
        _budgetSettings = new TranslationSaveBudgetSettings
        {
            SaveLimit = 10, SaveAlertThreshold = 3, OverwriteLimit = 10, OverwriteAlertThreshold = 10
        };
        _saveLedger.Seed(CurrentTranslator, saves: earlierSaves, overwrites: 0);
        Translation row = Untranslated();
        GivenStoredRow(row);
        GivenReadBack(row, "Witaj");

        // Act
        Result<TranslationDetailResponse> result = await CreateHandler().Handle(Command(1, "Witaj"), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        CapturingLoggerFactory.LogEntry warning = BudgetEntries().ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.EventId.Id.ShouldBe(EventIds.TranslationSaveBudgetWarning);
        warning.Message.ShouldStartWith(TranslationSaveBudgetLogText.BudgetPrefix);
        warning.Message.ShouldContain($"budget Saves, outcome AlertThreshold, {earlierSaves + 1} with this save");
    }

    [Fact]
    public async Task Handle_BelowTheSaveAlertThreshold_ShouldLogNoWarning()
    {
        // Arrange
        _budgetSettings = new TranslationSaveBudgetSettings
        {
            SaveLimit = 10, SaveAlertThreshold = 3, OverwriteLimit = 10, OverwriteAlertThreshold = 10
        };
        _saveLedger.Seed(CurrentTranslator, saves: 1, overwrites: 0);
        Translation row = Untranslated();
        GivenStoredRow(row);
        GivenReadBack(row, "Witaj");

        // Act
        await CreateHandler().Handle(Command(1, "Witaj"), CancellationToken.None);

        // Assert
        BudgetEntries().ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_AtTheOverwriteAlertThreshold_ShouldLogTheOverwriteWarning()
    {
        // Arrange
        _budgetSettings = new TranslationSaveBudgetSettings
        {
            SaveLimit = 10, SaveAlertThreshold = 10, OverwriteLimit = 5, OverwriteAlertThreshold = 2
        };
        _saveLedger.Seed(CurrentTranslator, saves: 1, overwrites: 1);
        Translation row = Translated(TranslationSeed.Draft);
        GivenStoredRow(row);
        GivenReadBack(row, "Nowy polski");

        // Act
        await CreateHandler().Handle(Command(1, "Nowy polski"), CancellationToken.None);

        // Assert
        BudgetEntries().ShouldHaveSingleItem().Message
            .ShouldContain("budget Overwrites, outcome AlertThreshold, 2 with this save, limit 5 per 24 h");
    }

    private UpsertTranslation.Handler CreateHandler(IApplicationReadDbContext? readDbContext = null)
        => new(
            new UpsertTranslation.Validator(),
            _translationRepository,
            _unitOfWork,
            _provisioner,
            readDbContext ?? new FakeReadDbContext(_readModels),
            TimeProvider.System,
            _rebuildScheduler,
            new TranslationSaveBudgetGate(
                new StubCurrentUserAccessor(ValueMaybe<IdentityId>.From(IdentityId.Create()), isAdmin: _callerIsAdmin),
                _saveLedger,
                Microsoft.Extensions.Options.Options.Create(_budgetSettings)),
            new Logger<UpsertTranslation.Handler>(_loggerFactory));

    private static UpsertTranslation.Command Command(int gossipId, string text)
        => new(FileId, gossipId, text);

    private static Translation Untranslated(int gossipId = 1, string source = "English")
        => Translation.CreateUntranslated(
            FragmentKey.Create(FileId, gossipId).Value,
            TranslationSource.Create(source, null, null).Value,
            VersionId,
            Now).Value;

    private void GivenStoredRow(Translation translation)
        => _translationRepository.GetByFragmentKeyAsync(Arg.Any<FragmentKey>(), Arg.Any<CancellationToken>())
            .Returns(Maybe<Translation>.From(translation));

    // Mirrors what the write just persisted, because the handler reads the response back through the
    // read model.
    private void GivenReadBack(Translation translation, string translatedText)
        => _readModels.Add(new TranslationReadModel(
            translation.Id,
            translation.FragmentKey.FileId,
            translation.FragmentKey.GossipId,
            translation.Source.Text,
            translation.Source.ArgsOrder,
            translation.Source.ArgsId,
            translatedText,
            translation.PreviousSourceText,
            CurrentTranslator,
            null,
            TranslationStatus.Draft,
            translation.IntroducedInVersion,
            translation.LastSourceChangeInVersion,
            translation.RemovedInVersion,
            translation.CreatedAt,
            Now)
        {
            SubmittedBy = new TranslatorReadModel(
                CurrentTranslator, default, SubmitterName, null, Now)
        });

    private static TranslationSaveBudgetSettings SmallBudget(int saveLimit, int overwriteLimit)
        => new()
        {
            SaveLimit = saveLimit,
            SaveAlertThreshold = saveLimit,
            OverwriteLimit = overwriteLimit,
            OverwriteAlertThreshold = overwriteLimit
        };

    private static Translation Translated(TranslationSeed seed)
    {
        Translation row = Untranslated();
        row.ProvideTranslation("Stary polski", CurrentTranslator, Now);
        if (seed is TranslationSeed.Approved)
        {
            row.Approve(CurrentTranslator, Now);
        }

        return row;
    }

    private List<CapturingLoggerFactory.LogEntry> BudgetEntries()
        => _loggerFactory.Entries
            .Where(entry => entry.EventId.Id == EventIds.TranslationSaveBudgetWarning)
            .ToList();

    private List<CapturingLoggerFactory.LogEntry> OverwriteEntries()
        => _loggerFactory.Entries
            .Where(entry => entry.EventId.Id == EventIds.TranslationOverwritten)
            .ToList();

    public enum TranslationSeed
    {
        Draft,
        Approved
    }

    public enum RejectedBeforeTheSave
    {
        InvalidText,
        UnknownRow,
        RemovedRow,
        PlaceholderMismatch
    }
}
