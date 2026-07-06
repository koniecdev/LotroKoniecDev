using System.Text;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Features.Import;
using LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;
using LotroKoniecDev.TranslationSystem.API.Parsing;
using LotroKoniecDev.TranslationSystem.Contracts.Import;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.Bulk;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using NSubstitute;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Features.Import;

public sealed class ImportExportedTextsHandlerTests
{
    private static readonly GameVersionId VersionId = GameVersionId.Create();

    // The validator and parser are pure and dependency-free, so they run for real; only the
    // genuine boundaries (repositories, unit of work) are stubbed.
    private readonly IGameVersionRepository _gameVersionRepository = Substitute.For<IGameVersionRepository>();
    private readonly ITranslationRepository _translationRepository = Substitute.For<ITranslationRepository>();
    private readonly IBulkTranslationInserter _bulkInserter = Substitute.For<IBulkTranslationInserter>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    public ImportExportedTextsHandlerTests()
    {
        _translationRepository.StreamSourceDigestsAsync(Arg.Any<CancellationToken>())
            .Returns(DigestStream());
        _translationRepository.GetByIdsAsync(Arg.Any<IReadOnlyList<TranslationId>>(), Arg.Any<CancellationToken>())
            .Returns([]);

        // No stacked older versions by default; the supersede tests override this per case.
        _gameVersionRepository.GetUnprocessedDetectedBeforeAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([]);

        // The transaction seam runs its operation for real against the stubbed boundaries, so the
        // success path still exercises the COPY + SaveChanges orchestration the handler wraps.
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.Arg<Func<CancellationToken, Task>>().Invoke(callInfo.Arg<CancellationToken>()));
    }

    private ImportExportedTexts.Handler CreateHandler(double maxRemovedFraction = 0.20)
        => new(
            new ImportExportedTexts.Validator(),
            new TranslationExportParser(),
            _gameVersionRepository,
            _translationRepository,
            _bulkInserter,
            _unitOfWork,
            TimeProvider.System,
            Microsoft.Extensions.Options.Options.Create(new ImportSettings { MaxRemovedFractionWithoutOverride = maxRemovedFraction }),
            new NoOpScheduler(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ImportExportedTexts.Handler>.Instance);

    // The scheduler is an internal interface (NSubstitute/Castle can't proxy it without a
    // DynamicProxyGenAssembly2 hook); a hand-written no-op keeps the import handler tests focused
    // on the diff, not on file regeneration (covered by the distribution integration tests).
    private sealed class NoOpScheduler : ITranslationFileRebuildScheduler
    {
        public void Schedule(string language)
        {
        }
    }

    private static ImportExportedTexts.Command Command(GameVersionId versionId, string export, bool allowMassRemoval = false)
        => new(versionId, new MemoryStream(Encoding.UTF8.GetBytes(export)), allowMassRemoval);

    private static string Line(int gossipId, string text) => $"620756992||{gossipId}||{text}||NULL||NULL||1";

    private static string Export(params string[] lines) => string.Join('\n', lines);

    private static GameVersion UnprocessedVersion()
        => GameVersion.Create(LotroNotationVersion.Create("48.0").Value, new DateTimeOffset(2026, 6, 13, 0, 0, 0, TimeSpan.Zero)).Value;

    private static GameVersion OlderUnprocessedVersion(string version, DateTimeOffset detectedAt)
        => GameVersion.Create(LotroNotationVersion.Create(version).Value, detectedAt).Value;

    private static Translation ExistingRow(int gossipId, string text)
        => Translation.CreateUntranslated(
            FragmentKey.Create(620756992, gossipId).Value,
            TranslationSource.Create(text, null, null).Value,
            VersionId,
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)).Value;

    private static async IAsyncEnumerable<StoredSourceDigest> DigestStream(params Translation[] translations)
    {
        await Task.Yield();
        foreach (Translation translation in translations)
        {
            yield return new StoredSourceDigest(
                translation.Id,
                FragmentKeyValue.From(translation.FragmentKey),
                SourceHash.Compute(translation.Source),
                translation.Status,
                translation.IsRemoved);
        }
    }

    private void GivenVersion(GameVersion gameVersion)
        => _gameVersionRepository.GetByIdAsync(VersionId, Arg.Any<CancellationToken>())
            .Returns(Maybe<GameVersion>.From(gameVersion));

    private void GivenExisting(params Translation[] translations)
        => _translationRepository.StreamSourceDigestsAsync(Arg.Any<CancellationToken>())
            .Returns(DigestStream(translations));

    [Fact]
    public async Task Handle_WhenCommandInvalid_ShouldReturnValidationError()
    {
        // Arrange — a default (empty) version id fails the command validator.
        ImportExportedTexts.Command command = Command(default, Export(Line(1, "A")));

        // Act
        Result<ImportSummary> result = await CreateHandler().Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Import.Validation");
    }

    [Fact]
    public async Task Handle_WhenVersionNotFound_ShouldReturnNotFound()
    {
        // Arrange
        _gameVersionRepository.GetByIdAsync(VersionId, Arg.Any<CancellationToken>())
            .Returns(Maybe<GameVersion>.None);

        // Act
        Result<ImportSummary> result = await CreateHandler().Handle(Command(VersionId, Export(Line(1, "A"))), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("GameVersionEntity.NotFound");
    }

    [Fact]
    public async Task Handle_WhenParseHasErrors_ShouldRejectAndNotPersist()
    {
        // Arrange — the second line is missing its trailing fields.
        GivenVersion(UnprocessedVersion());
        string export = Export(Line(1, "A"), "620756992||2||truncated line");

        // Act
        Result<ImportSummary> result = await CreateHandler().Handle(Command(VersionId, export), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Import.ParseFailed");
        await _unitOfWork.DidNotReceive().ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenUploadHasMoreBadLinesThanTheCap_ShouldRejectWithCappedErrorCount()
    {
        // Arrange — 150 unparseable lines; the import stops collecting at the 100-error cap
        // (spec 0006) but still rejects the whole upload.
        GivenVersion(UnprocessedVersion());
        string export = Export([.. Enumerable.Range(1, 150).Select(index => $"620756992||{index}||missing trailing fields")]);

        // Act
        Result<ImportSummary> result = await CreateHandler().Handle(Command(VersionId, export), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Import.ParseFailed");
        result.Error.Message.ShouldContain("100 unparseable");
        await _unitOfWork.DidNotReceive().ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenMassRemovalExceedsThresholdWithoutOverride_ShouldBlockAndNotPersist()
    {
        // Arrange — five active rows, the upload keeps three (40% would be removed).
        GivenVersion(UnprocessedVersion());
        GivenExisting(ExistingRow(1, "A"), ExistingRow(2, "B"), ExistingRow(3, "C"), ExistingRow(4, "D"), ExistingRow(5, "E"));
        string export = Export(Line(1, "A"), Line(2, "B"), Line(3, "C"));

        // Act
        Result<ImportSummary> result = await CreateHandler().Handle(Command(VersionId, export, allowMassRemoval: false), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Import.MassRemovalBlocked");
        await _unitOfWork.DidNotReceive().ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenMassRemovalWithOverride_ShouldPersistRemovals()
    {
        // Arrange
        GivenVersion(UnprocessedVersion());
        GivenExisting(ExistingRow(1, "A"), ExistingRow(2, "B"), ExistingRow(3, "C"), ExistingRow(4, "D"), ExistingRow(5, "E"));
        string export = Export(Line(1, "A"), Line(2, "B"), Line(3, "C"));

        // Act
        Result<ImportSummary> result = await CreateHandler().Handle(Command(VersionId, export, allowMassRemoval: true), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Removed.ShouldBe(2);
        result.Value.Unchanged.ShouldBe(3);
    }

    [Fact]
    public async Task Handle_WhenRemovedFractionEqualsThreshold_ShouldSucceedWithoutOverride()
    {
        // Arrange — five active rows, the upload drops exactly one (20%); "exceeds" is strict.
        GivenVersion(UnprocessedVersion());
        GivenExisting(ExistingRow(1, "A"), ExistingRow(2, "B"), ExistingRow(3, "C"), ExistingRow(4, "D"), ExistingRow(5, "E"));
        string export = Export(Line(1, "A"), Line(2, "B"), Line(3, "C"), Line(4, "D"));

        // Act
        Result<ImportSummary> result = await CreateHandler().Handle(Command(VersionId, export, allowMassRemoval: false), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Removed.ShouldBe(1);
    }

    [Fact]
    public async Task Handle_WhenRowFailsValueObjectValidation_ShouldReturnInvalidRow()
    {
        // Arrange — file id 0 parses but fails FragmentKey validation.
        GivenVersion(UnprocessedVersion());

        // Act
        Result<ImportSummary> result = await CreateHandler().Handle(
            Command(VersionId, "0||1||Text||NULL||NULL||1"), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Import.InvalidRow");
        await _unitOfWork.DidNotReceive().ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenUploadHasDuplicateFragmentKey_ShouldReturnDuplicateError()
    {
        // Arrange — two rows share (FileId, GossipId).
        GivenVersion(UnprocessedVersion());

        // Act
        Result<ImportSummary> result = await CreateHandler().Handle(
            Command(VersionId, Export(Line(1, "A"), Line(1, "B"))), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Import.DuplicateFragmentKey");
        await _unitOfWork.DidNotReceive().ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenVersionIsSuperseded_ShouldRejectAndNotPersist()
    {
        // Arrange
        GameVersion superseded = UnprocessedVersion();
        superseded.MarkSuperseded();
        GivenVersion(superseded);

        // Act
        Result<ImportSummary> result = await CreateHandler().Handle(
            Command(VersionId, Export(Line(1, "A"))), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("GameVersionEntity.SupersededCannotBeProcessed");
        await _unitOfWork.DidNotReceive().ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenUploadHasNoRows_ShouldRejectAndNotPersist()
    {
        // Arrange — a comments-and-blanks-only file parses cleanly to zero rows.
        GivenVersion(UnprocessedVersion());

        // Act
        Result<ImportSummary> result = await CreateHandler().Handle(
            Command(VersionId, "# only a comment\n   \n"), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Import.EmptyUpload");
        await _unitOfWork.DidNotReceive().ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_OnBaselineImport_ShouldAddEveryRowAndMarkVersionProcessed()
    {
        // Arrange
        GameVersion gameVersion = UnprocessedVersion();
        GivenVersion(gameVersion);
        string export = Export(Line(1, "A"), Line(2, "B"));

        // Act
        Result<ImportSummary> result = await CreateHandler().Handle(Command(VersionId, export), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Added.ShouldBe(2);
        gameVersion.Status.ShouldBe(GameVersionStatus.Processed);
    }

    [Fact]
    public async Task Handle_WhenOlderUnprocessedVersionsExist_ShouldMarkThemSupersededAndWarn()
    {
        // Arrange — the target is processed while two older versions are still unprocessed. The
        // stub keys on the target's DetectedAt, so this also pins that the handler queries with the
        // processed version's timestamp (a wrong argument falls through to the default empty stub).
        GameVersion target = UnprocessedVersion();
        GivenVersion(target);
        GameVersion olderA = OlderUnprocessedVersion("47.2", new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        GameVersion olderB = OlderUnprocessedVersion("47.3", new DateTimeOffset(2026, 6, 5, 0, 0, 0, TimeSpan.Zero));
        _gameVersionRepository.GetUnprocessedDetectedBeforeAsync(target.DetectedAt, Arg.Any<CancellationToken>())
            .Returns([olderA, olderB]);

        // Act
        Result<ImportSummary> result = await CreateHandler().Handle(
            Command(VersionId, Export(Line(1, "A"))), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        target.Status.ShouldBe(GameVersionStatus.Processed);
        olderA.Status.ShouldBe(GameVersionStatus.Superseded);
        olderB.Status.ShouldBe(GameVersionStatus.Superseded);
        result.Value.Warnings.ShouldContain(warning => warning.Contains("2 older unprocessed version"));
    }

    [Fact]
    public async Task Handle_WhenNoOlderUnprocessedVersions_ShouldNotEmitSupersedeWarning()
    {
        // Arrange — the default stub returns no older versions, so a plain baseline import warns nothing.
        GivenVersion(UnprocessedVersion());

        // Act
        Result<ImportSummary> result = await CreateHandler().Handle(
            Command(VersionId, Export(Line(1, "A"))), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Warnings.ShouldNotContain(warning => warning.Contains("superseded"));
    }
}
