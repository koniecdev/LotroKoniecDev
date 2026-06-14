using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Enums;
using LotroKoniecDev.SharedKernel.Monads;
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
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.TranslatorAggregate;
using NSubstitute;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Features.Translations;

public sealed class UpsertTranslationHandlerTests
{
    private const int FileId = 620756992;
    private const string SubmitterName = "Aragorn";
    private static readonly DateTimeOffset Now = new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly GameVersionId VersionId = GameVersionId.Create();
    private static readonly TranslatorId CurrentTranslator = TranslatorId.Create();

    // ITranslationRepository / IUnitOfWork are genuine public boundaries (stubbed); the read context
    // is a pure in-memory double serving the response read-back, and the provisioner + artifact
    // builder are internal interfaces NSubstitute can't proxy, so each gets a hand-written double.
    private readonly ITranslationRepository _translationRepository = Substitute.For<ITranslationRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly List<TranslationReadModel> _readModels = [];

    private readonly RecordingProjector _projector = new();

    private StubTranslatorProvisioner _provisioner = new(Result.Success(CurrentTranslator));

    private UpsertTranslation.Handler CreateHandler(IApplicationReadDbContext? readDbContext = null)
        => new(
            new UpsertTranslation.Validator(),
            _translationRepository,
            _unitOfWork,
            _provisioner,
            readDbContext ?? new FakeReadDbContext(_readModels),
            TimeProvider.System,
            _projector);

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

    // The handler re-reads the committed row through the read model for the response, so the seeded
    // read model mirrors what the write just persisted (status Draft, the stamped submitter).
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
                CurrentTranslator, default, SubmitterName, null, Now, Now)
        });

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
    public async Task Handle_WhenProvisioningFails_ShouldReturnFailureAndNotPersist()
    {
        // Arrange — a token without a parseable subject must never be attributed; the provisioner
        // surfaces that, and the handler must not stamp or persist.
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
        // Arrange — a soft-removed row is excluded from translation work (spec 0001).
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
        // The aggregate itself was stamped with the provisioned translator id.
        row.SubmittedById.ShouldBe(CurrentTranslator);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        // Editing a non-Approved row does not change the distributed set, so no artifact rebuild.
        _projector.RebuildCount.ShouldBe(0);
    }

    [Fact]
    public async Task Handle_OnApprovedRow_ShouldMoveToDraftAndRebuildArtifact()
    {
        // Arrange — an Approved row is in the distributed file; editing pulls it out (spec 0001 Q1).
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
        _projector.RebuildCount.ShouldBe(1);
    }

    [Fact]
    public async Task Handle_OnNeedsReviewRow_ShouldMoveToDraftAndKeepPreviousSource()
    {
        // Arrange — an invalidated row keeps its superseded English until approve (the re-translation path).
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
        _projector.RebuildCount.ShouldBe(0);
    }

    private sealed class RecordingProjector : IPrecomputedTranslationFileProjector
    {
        public int RebuildCount { get; private set; }

        public Task RebuildAsync(string language, CancellationToken cancellationToken)
        {
            RebuildCount++;
            return Task.CompletedTask;
        }
    }
}
