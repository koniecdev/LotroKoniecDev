using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Enums;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.Tests.Shared;
using LotroKoniecDev.TranslationSystem.API;
using LotroKoniecDev.TranslationSystem.API.Features.Translations;
using LotroKoniecDev.TranslationSystem.API.Tests.Unit.Shared;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;
using LotroKoniecDev.TranslationSystem.Persistence.SaveBudget;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.TranslationAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Npgsql;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Features.Translations;

public sealed class FillIdenticalTranslationsHandlerTests
{
    private const int FileId = 620756997;
    private const string Source = "Wooden Spear[E]";
    private const string Polish = "Drewniana włócznia";
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FillTime = new(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);
    private static readonly GameVersionId VersionId = GameVersionId.Create();
    private static readonly TranslatorId CurrentTranslator = TranslatorId.Create();

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

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Handle_WhenTheTextIsBlank_ShouldReturnAValidationErrorAndSpendNothing(string typedText)
    {
        // Arrange
        Translation row = Row(1);
        GivenRows(row, Row(2));

        // Act
        Result<FillIdenticalTranslationsResponse> result = await Fill(row, typedText);

        // Assert
        result.Error.Code.ShouldBe("Translations.Validation");
        _saveLedger.RecordCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Handle_WhenTheRowIsUnknown_ShouldReturnNotFound()
    {
        // Arrange
        _translationRepository.GetByIdAsync(Arg.Any<TranslationId>(), Arg.Any<CancellationToken>())
            .Returns(Maybe<Translation>.None);

        // Act
        Result<FillIdenticalTranslationsResponse> result = await CreateHandler().Handle(
            new FillIdenticalTranslations.Command(TranslationId.Create(), Polish), CancellationToken.None);

        // Assert
        result.Error.Code.ShouldBe("TranslationEntity.NotFound");
    }

    [Fact]
    public async Task Handle_WhenTheRowIsRemoved_ShouldRefuseAndSpendNothing()
    {
        // Arrange
        Translation row = Row(1);
        row.MarkRemoved(VersionId, Now);
        GivenRows(row, Row(2));

        // Act
        Result<FillIdenticalTranslationsResponse> result = await Fill(row, Polish);

        // Assert
        result.Error.Code.ShouldBe("TranslationEntity.CannotEditRemoved");
        _saveLedger.RecordCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Handle_WhenThePlaceholderCountDiffersFromTheEnglish_ShouldRefuseBeforeAnyPermit()
    {
        // Arrange
        Translation row = Row(1, source: "Talk to <--DO_NOT_TOUCH!-->");
        Translation twin = Row(2, source: "Talk to <--DO_NOT_TOUCH!-->");
        GivenRows(row, twin);

        // Act
        Result<FillIdenticalTranslationsResponse> result = await Fill(row, "Porozmawiaj");

        // Assert
        result.Error.Code.ShouldBe("TranslationEntity.TranslatedText.PlaceholderCountMismatch");
        _saveLedger.RecordCalls.ShouldBe(0);
        twin.TranslatedText.ShouldBeNull();
    }

    [Fact]
    public async Task Handle_OnAnUntranslatedRowWithTwins_ShouldWriteEveryRowAsADraftOfTheCaller()
    {
        // Arrange
        Translation row = Row(1);
        Translation[] twins = [Row(2), Row(3)];
        GivenRows(row, twins);

        // Act
        Result<FillIdenticalTranslationsResponse> result = await Fill(row, Polish);

        // Assert
        result.Value.FilledCount.ShouldBe(2);
        result.Value.TranslationSaved.ShouldBeTrue();
        new[] { row }.Concat(twins).ShouldAllBe(translation => translation.TranslatedText == Polish
            && translation.Status == TranslationStatus.Draft
            && translation.SubmittedById == CurrentTranslator);
        await _unitOfWork.Received(1).SaveChangesInTransactionAsync(
            Arg.Any<Func<CancellationToken, Task<bool>>>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Nowa włócznia", 3, 1)]
    [InlineData("Stara włócznia", 2, 0)]
    [InlineData("Stara włócznia\n", 2, 0)]
    public async Task Handle_AsAPlainTranslator_ShouldTakeOnePermitPerSavedRowAndOneOverwriteAtMost(
        string typedText,
        int expectedSaves,
        int expectedOverwrites)
    {
        // Arrange: only this row has Polish, so only its own save can be an overwrite.
        Translation row = Row(1, polish: "Stara włócznia");
        GivenRows(row, Row(2), Row(3));

        // Act
        Result<FillIdenticalTranslationsResponse> result = await Fill(row, typedText);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        _saveLedger.CountFor(CurrentTranslator).ShouldBe(new TranslationSaveCounts(expectedSaves, expectedOverwrites));
        _saveLedger.RecordCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Handle_WithTheTextAnApprovedRowAlreadyHas_ShouldKeepTheRowApprovedAndNotRebuild()
    {
        // Arrange
        Translation row = Row(1, polish: Polish, approve: true);
        GivenRows(row, Row(2));

        // Act
        Result<FillIdenticalTranslationsResponse> result = await Fill(row, Polish);

        // Assert
        result.Value.TranslationSaved.ShouldBeFalse();
        result.Value.FilledCount.ShouldBe(1);
        row.Status.ShouldBe(TranslationStatus.Approved);
        _rebuildScheduler.ScheduleCount.ShouldBe(0);
        OverwriteEntries().ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_WithANewTextOnAnApprovedRow_ShouldRebuildOnceAfterTheCommitAndLogTheOverwrite()
    {
        // Arrange
        Translation row = Row(1, polish: "Stara włócznia", approve: true);
        GivenRows(row, Row(2));
        _unitOfWork.When(unitOfWork => unitOfWork.SaveChangesInTransactionAsync(
                Arg.Any<Func<CancellationToken, Task<bool>>>(), Arg.Any<CancellationToken>()))
            .Do(_ => _callOrder.Add(nameof(IUnitOfWork.SaveChangesInTransactionAsync)));

        // Act
        Result<FillIdenticalTranslationsResponse> result = await Fill(row, Polish);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        _callOrder.ShouldBe([nameof(IUnitOfWork.SaveChangesInTransactionAsync), "Schedule"]);
        OverwriteEntries().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Handle_OnAnUntranslatedRow_ShouldLogNoOverwrite()
    {
        // Arrange
        Translation row = Row(1);
        GivenRows(row, Row(2));

        // Act
        await Fill(row, Polish);

        // Assert
        OverwriteEntries().ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_WithAnUnchangedTextAndNoEmptyRow_ShouldWriteNothingAndTakeNoPermit()
    {
        // Arrange
        Translation row = Row(1, polish: Polish);
        GivenRows(row);

        // Act
        Result<FillIdenticalTranslationsResponse> result = await Fill(row, Polish);

        // Assert
        result.Value.FilledCount.ShouldBe(0);
        result.Value.TranslationSaved.ShouldBeFalse();
        _saveLedger.RecordCalls.ShouldBe(0);
        await _unitOfWork.DidNotReceive().SaveChangesInTransactionAsync(
            Arg.Any<Func<CancellationToken, Task<bool>>>(), Arg.Any<CancellationToken>());
        FilledEntries().ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_WhenTheCopyDoesNotFit_ShouldAnswerWithTheSaveCodeAndChangeNoRow()
    {
        // Arrange: two saves left, three needed.
        _budgetSettings = SmallBudget(saveLimit: 3);
        _saveLedger.Seed(CurrentTranslator, saves: 1, overwrites: 0);
        Translation row = Row(1);
        Translation[] twins = [Row(2), Row(3)];
        GivenRows(row, twins);

        // Act
        Result<FillIdenticalTranslationsResponse> result = await Fill(row, Polish);

        // Assert
        result.Error.Code.ShouldBe("Translations.SaveBudgetExhausted");
        result.Error.Message.ShouldContain("3 rows");
        _saveLedger.RecordCalls.ShouldBe(0);
        _saveLedger.CountFor(CurrentTranslator).ShouldBe(new TranslationSaveCounts(1, 0));
        new[] { row }.Concat(twins).ShouldAllBe(translation => translation.TranslatedText == null);
        BudgetEntries().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Handle_WhenTheCopyCannotFit_ShouldRefuseBeforeLoadingARowOrWritingAPermit()
    {
        // Arrange: a refused fill must stay cheap however large its group, because it can be sent again
        // and again (ADR-0072 amendment of 2026-11-27).
        _budgetSettings = SmallBudget(saveLimit: 3);
        Translation row = Row(1);
        GivenRows(row, Row(2), Row(3), Row(4));

        // Act
        Result<FillIdenticalTranslationsResponse> result = await Fill(row, Polish);

        // Assert
        result.Error.Code.ShouldBe("Translations.SaveBudgetExhausted");
        result.Error.Message.ShouldContain("4 rows");
        _saveLedger.RecordCalls.ShouldBe(0);
        await _translationRepository.DidNotReceive().GetEmptyRowsWithSourceAsync(
            Arg.Any<TranslationSource>(), Arg.Any<TranslationId>(), Arg.Any<CancellationToken>());
        BudgetEntries().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Handle_WhenParallelSavesUseTheRoomAfterTheCheck_ShouldStillRefuseAndHandEveryPermitBack()
    {
        // Arrange: the early check sees room for three; two parallel saves take it before the permits land.
        _budgetSettings = SmallBudget(saveLimit: 3);
        _saveLedger.ParallelSavesBeforeRecord = 2;
        Translation row = Row(1);
        Translation[] twins = [Row(2), Row(3)];
        GivenRows(row, twins);

        // Act
        Result<FillIdenticalTranslationsResponse> result = await Fill(row, Polish);

        // Assert
        result.Error.Code.ShouldBe("Translations.SaveBudgetExhausted");
        _saveLedger.CountFor(CurrentTranslator).ShouldBe(new TranslationSaveCounts(2, 0));
        twins.ShouldAllBe(translation => translation.TranslatedText == null);
    }

    [Fact]
    public async Task Handle_WhenATwinIsFilledBetweenTheCountAndTheLoad_ShouldFillAndCountOnlyTheLoadedRows()
    {
        // Arrange: the count saw three empty twins; by the load, two had been filled by someone else.
        Translation row = Row(1);
        Translation twin = Row(2);
        GivenRows(row, twin);
        _translationRepository.CountEmptyRowsWithSourceAsync(row.Source, row.Id, Arg.Any<CancellationToken>())
            .Returns(3);

        // Act
        Result<FillIdenticalTranslationsResponse> result = await Fill(row, Polish);

        // Assert
        result.Value.FilledCount.ShouldBe(1);
        _saveLedger.CountFor(CurrentTranslator).ShouldBe(new TranslationSaveCounts(2, 0));
        FilledEntries().ShouldHaveSingleItem().Message.ShouldContain("into 1 empty rows");
    }

    [Fact]
    public async Task Handle_WhenEveryTwinIsFilledBetweenTheCountAndTheLoadAndTheTextIsStored_ShouldWriteNothing()
    {
        // Arrange
        Translation row = Row(1, polish: Polish);
        GivenRows(row);
        _translationRepository.CountEmptyRowsWithSourceAsync(row.Source, row.Id, Arg.Any<CancellationToken>())
            .Returns(2);

        // Act
        Result<FillIdenticalTranslationsResponse> result = await Fill(row, Polish);

        // Assert
        result.Value.FilledCount.ShouldBe(0);
        result.Value.TranslationSaved.ShouldBeFalse();
        _saveLedger.RecordCalls.ShouldBe(0);
        await _unitOfWork.DidNotReceive().SaveChangesInTransactionAsync(
            Arg.Any<Func<CancellationToken, Task<bool>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenMoreRowsAreLoadedThanCounted_ShouldLetThePermitsDecide()
    {
        // Arrange: the early check saw room for two saves; the load finds four, which do not fit.
        _budgetSettings = SmallBudget(saveLimit: 3);
        Translation row = Row(1);
        Translation[] twins = [Row(2), Row(3), Row(4)];
        GivenRows(row, twins);
        _translationRepository.CountEmptyRowsWithSourceAsync(row.Source, row.Id, Arg.Any<CancellationToken>())
            .Returns(1);

        // Act
        Result<FillIdenticalTranslationsResponse> result = await Fill(row, Polish);

        // Assert
        result.Error.Code.ShouldBe("Translations.SaveBudgetExhausted");
        _saveLedger.RecordCalls.ShouldBe(1);
        _saveLedger.CountFor(CurrentTranslator).ShouldBe(new TranslationSaveCounts(0, 0));
        twins.ShouldAllBe(translation => translation.TranslatedText == null);
    }

    [Fact]
    public async Task Handle_WhenTheCallerIsAnAdmin_ShouldNeitherSpendNorBeRefused()
    {
        // Arrange
        _callerIsAdmin = true;
        _budgetSettings = SmallBudget(saveLimit: 1);
        Translation row = Row(1);
        GivenRows(row, Row(2), Row(3));

        // Act
        Result<FillIdenticalTranslationsResponse> result = await Fill(row, Polish);

        // Assert
        result.Value.FilledCount.ShouldBe(2);
        _saveLedger.RecordCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Handle_WhenTheWriteFails_ShouldHandEveryPermitBackAndRethrow()
    {
        // Arrange: a 409 on one row changes no row, so the action spent nothing (ADR-0072 amendment).
        Translation row = Row(1);
        GivenRows(row, Row(2), Row(3));
        _unitOfWork.SaveChangesInTransactionAsync(Arg.Any<Func<CancellationToken, Task<bool>>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DbUpdateConcurrencyException("a parallel save"));

        // Act
        await Should.ThrowAsync<DbUpdateConcurrencyException>(async () => await Fill(row, Polish));

        // Assert
        _saveLedger.CountFor(CurrentTranslator).ShouldBe(new TranslationSaveCounts(0, 0));
        OverwriteEntries().ShouldBeEmpty();
        FilledEntries().ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_WhenThePermitStepFailsAfterItsInsert_ShouldHandEveryPermitBackAndRethrow()
    {
        // Arrange: the permits are stored, then the count breaks; nothing was written to the rows.
        Translation row = Row(1);
        Translation[] twins = [Row(2), Row(3)];
        GivenRows(row, twins);
        _saveLedger.RecordFailure = new NpgsqlException("the count broke");

        // Act
        await Should.ThrowAsync<NpgsqlException>(async () => await Fill(row, Polish));

        // Assert
        _saveLedger.CountFor(CurrentTranslator).ShouldBe(new TranslationSaveCounts(0, 0));
        twins.ShouldAllBe(translation => translation.TranslatedText == null);
    }

    [Fact]
    public async Task Handle_WhenOnlyTheOverwriteBudgetIsSpent_ShouldRefuseAFillThatReplacesThisRowsPolish()
    {
        // Arrange: this row has Polish, so its own save is the fill's one overwrite.
        _budgetSettings = new TranslationSaveBudgetSettings
        {
            SaveLimit = 100,
            SaveAlertThreshold = 100,
            OverwriteLimit = 1,
            OverwriteAlertThreshold = 1
        };
        _saveLedger.Seed(CurrentTranslator, saves: 1, overwrites: 1);
        Translation row = Row(1, polish: "Stara włócznia");
        Translation[] twins = [Row(2), Row(3)];
        GivenRows(row, twins);

        // Act
        Result<FillIdenticalTranslationsResponse> result = await Fill(row, Polish);

        // Assert
        result.Error.Code.ShouldBe("Translations.OverwriteBudgetExhausted");
        _saveLedger.CountFor(CurrentTranslator).ShouldBe(new TranslationSaveCounts(1, 1));
        twins.ShouldAllBe(translation => translation.TranslatedText == null);
        row.TranslatedText.ShouldBe("Stara włócznia");
    }

    [Fact]
    public async Task Handle_WhenProvisioningFails_ShouldReturnItsErrorAndSpendNothing()
    {
        // Arrange: a token without a usable subject must never be attributed.
        Translation row = Row(1);
        Translation[] twins = [Row(2)];
        GivenRows(row, twins);
        _provisioner = new StubTranslatorProvisioner(Result.Failure<TranslatorId>(new Error(
            "Translators.Unauthenticated", "no subject", TypeOfError.Forbidden)));

        // Act
        Result<FillIdenticalTranslationsResponse> result = await Fill(row, Polish);

        // Assert
        result.Error.Code.ShouldBe("Translators.Unauthenticated");
        _saveLedger.RecordCalls.ShouldBe(0);
        twins.ShouldAllBe(translation => translation.TranslatedText == null);
    }

    [Fact]
    public async Task Handle_WhenTheHandBackFailsToo_ShouldLogAWarningAndRethrowTheWritesError()
    {
        // Arrange
        Translation row = Row(1);
        GivenRows(row, Row(2));
        _unitOfWork.SaveChangesInTransactionAsync(Arg.Any<Func<CancellationToken, Task<bool>>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DbUpdateConcurrencyException("a parallel save"));
        _saveLedger.RemoveFailure = new NpgsqlException("database down");

        // Act
        await Should.ThrowAsync<DbUpdateConcurrencyException>(async () => await Fill(row, Polish));

        // Assert
        List<CapturingLoggerFactory.LogEntry> warnings = _loggerFactory.Entries
            .Where(entry => entry.EventId.Id == EventIds.TranslationSavePermitsNotHandedBack)
            .ToList();
        warnings.Count.ShouldBe(1);
        warnings[0].Level.ShouldBe(LogLevel.Warning);
    }

    [Fact]
    public async Task Handle_WhenTheWriteOfAnAdminFails_ShouldHandNothingBackAndLogNoWarning()
    {
        // Arrange: a caller with no budget took no permit, so there is nothing to give back.
        _callerIsAdmin = true;
        Translation row = Row(1);
        GivenRows(row, Row(2));
        _unitOfWork.SaveChangesInTransactionAsync(Arg.Any<Func<CancellationToken, Task<bool>>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DbUpdateConcurrencyException("a parallel save"));
        _saveLedger.RemoveFailure = new NpgsqlException("database down");

        // Act
        await Should.ThrowAsync<DbUpdateConcurrencyException>(async () => await Fill(row, Polish));

        // Assert
        _loggerFactory.Entries.ShouldNotContain(entry => entry.EventId.Id == EventIds.TranslationSavePermitsNotHandedBack);
    }

    [Fact]
    public async Task Handle_WhenTheClientLeavesAfterThePermitsAreTaken_ShouldStillFillEveryRow()
    {
        // Arrange: nothing from the permits to the commit listens to the request's token.
        Translation row = Row(1);
        Translation[] twins = [Row(2), Row(3)];
        GivenRows(row, twins);
        using CancellationTokenSource clientLeaves = new();
        _saveLedger.CancelAfterRecord = clientLeaves;

        // Act
        await CreateHandler().Handle(new FillIdenticalTranslations.Command(row.Id, Polish), clientLeaves.Token);

        // Assert
        await _unitOfWork.Received(1).SaveChangesInTransactionAsync(
            Arg.Any<Func<CancellationToken, Task<bool>>>(), CancellationToken.None);
        twins.ShouldAllBe(translation => translation.TranslatedText == Polish);
    }

    [Fact]
    public async Task Handle_AtTheAlertThreshold_ShouldLogOneWarningForTheWholeFill()
    {
        // Arrange
        _budgetSettings = new TranslationSaveBudgetSettings
        {
            SaveLimit = 100,
            SaveAlertThreshold = 3,
            OverwriteLimit = 50,
            OverwriteAlertThreshold = 50
        };
        Translation row = Row(1);
        GivenRows(row, Row(2), Row(3), Row(4));

        // Act
        await Fill(row, Polish);

        // Assert
        BudgetEntries().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Handle_ShouldLogOneInformationLineWithTheRowAndTheFilledCount()
    {
        // Arrange
        Translation row = Row(1);
        GivenRows(row, Row(2), Row(3));

        // Act
        await Fill(row, Polish);

        // Assert
        List<CapturingLoggerFactory.LogEntry> filled = FilledEntries();
        filled.Count.ShouldBe(1);
        filled[0].Level.ShouldBe(LogLevel.Information);
        filled[0].Message.ShouldContain($"({FileId}, 1) into 2 empty rows");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Handle_ShouldCheckALostCommitWithTheStampOfARowItWrote(bool commitLanded)
    {
        // Arrange: this row is unchanged, so the stamp comes from the filled twin. The clock is fixed, so the
        // twin's stored stamp can be written before the fill runs.
        Translation row = Row(1, polish: Polish);
        Translation twin = Row(2);
        GivenRows(row, twin);
        _readModels.Add(ReadModelOf(twin, CurrentTranslator, commitLanded ? FillTime : Now));
        Func<CancellationToken, Task<bool>>? verifySucceeded = null;
        _unitOfWork.When(unitOfWork => unitOfWork.SaveChangesInTransactionAsync(
                Arg.Any<Func<CancellationToken, Task<bool>>>(), Arg.Any<CancellationToken>()))
            .Do(call => verifySucceeded = call.Arg<Func<CancellationToken, Task<bool>>>());
        await CreateHandler(new FakeTimeProvider(FillTime))
            .Handle(new FillIdenticalTranslations.Command(row.Id, Polish), CancellationToken.None);

        // Act
        bool landed = await verifySucceeded!(CancellationToken.None);

        // Assert
        landed.ShouldBe(commitLanded);
    }

    public FillIdenticalTranslationsHandlerTests()
    {
        _rebuildScheduler = new RecordingRebuildScheduler(_callOrder);
    }

    private FillIdenticalTranslations.Handler CreateHandler(TimeProvider? timeProvider = null)
        => new(
            new FillIdenticalTranslations.Validator(),
            _translationRepository,
            _unitOfWork,
            _provisioner,
            new FakeReadDbContext(_readModels),
            timeProvider ?? TimeProvider.System,
            _rebuildScheduler,
            new TranslationSaveBudgetGate(
                new StubCurrentUserAccessor(ValueMaybe<IdentityId>.From(IdentityId.Create()), isAdmin: _callerIsAdmin),
                _saveLedger,
                Microsoft.Extensions.Options.Options.Create(_budgetSettings)),
            new Logger<FillIdenticalTranslations.Handler>(_loggerFactory));

    private Task<Result<FillIdenticalTranslationsResponse>> Fill(Translation row, string typedText)
        => CreateHandler().Handle(new FillIdenticalTranslations.Command(row.Id, typedText), CancellationToken.None).AsTask();

    /// <summary>
    /// The stored row answers the load, the twins answer the empty-row query, and the read model answers
    /// the read-back.
    /// </summary>
    private void GivenRows(Translation row, params Translation[] twins)
    {
        _translationRepository.GetByIdAsync(row.Id, Arg.Any<CancellationToken>())
            .Returns(Maybe<Translation>.From(row));
        _translationRepository.GetEmptyRowsWithSourceAsync(row.Source, row.Id, Arg.Any<CancellationToken>())
            .Returns(twins);
        _translationRepository.CountEmptyRowsWithSourceAsync(row.Source, row.Id, Arg.Any<CancellationToken>())
            .Returns(twins.Length);
        _readModels.Add(ReadModelOf(row, CurrentTranslator, Now));
    }

    private static Translation Row(long gossipId, string source = Source, string? polish = null, bool approve = false)
    {
        Translation row = Translation.CreateUntranslated(
            FragmentKey.Create(FileId, gossipId).Value,
            TranslationSource.Create(source, null, null).Value,
            VersionId,
            Now).Value;

        if (polish is not null)
        {
            row.ProvideTranslation(polish, CurrentTranslator, Now);
        }

        if (approve)
        {
            row.Approve(CurrentTranslator, Now);
        }

        return row;
    }

    private static TranslationReadModel ReadModelOf(Translation translation, TranslatorId? submittedById, DateTimeOffset updatedAt)
        => new(
            translation.Id,
            translation.FragmentKey.FileId,
            translation.FragmentKey.GossipId,
            translation.Source.Text,
            translation.Source.ArgsOrder,
            translation.Source.ArgsId,
            translation.TranslatedText,
            translation.PreviousSourceText,
            submittedById,
            null,
            translation.Status,
            translation.IntroducedInVersion,
            translation.LastSourceChangeInVersion,
            translation.RemovedInVersion,
            translation.CreatedAt,
            updatedAt);

    private static TranslationSaveBudgetSettings SmallBudget(int saveLimit)
        => new()
        {
            SaveLimit = saveLimit,
            SaveAlertThreshold = saveLimit,
            OverwriteLimit = saveLimit,
            OverwriteAlertThreshold = saveLimit
        };

    private List<CapturingLoggerFactory.LogEntry> BudgetEntries()
        => _loggerFactory.Entries
            .Where(entry => entry.EventId.Id == EventIds.TranslationSaveBudgetWarning)
            .ToList();

    private List<CapturingLoggerFactory.LogEntry> OverwriteEntries()
        => _loggerFactory.Entries
            .Where(entry => entry.EventId.Id == EventIds.TranslationOverwritten)
            .ToList();

    private List<CapturingLoggerFactory.LogEntry> FilledEntries()
        => _loggerFactory.Entries
            .Where(entry => entry.EventId.Id == EventIds.IdenticalRowsFilled)
            .ToList();
}
