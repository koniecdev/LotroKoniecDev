using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.API.Auth.CurrentUserAccessing;
using LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;
using LotroKoniecDev.TranslationSystem.API.Features.Translations;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using NSubstitute;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Features.Translations;

public sealed class UpsertTranslationHandlerTests
{
    private const int FileId = 620756992;
    private static readonly DateTimeOffset Now = new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly GameVersionId VersionId = GameVersionId.Create();
    private static readonly IdentityId CurrentUser = IdentityId.Create();

    // ITranslationRepository / IUnitOfWork are genuine public boundaries (stubbed); the current-user
    // accessor and the artifact builder are internal interfaces NSubstitute can't proxy, so each gets
    // a focused hand-written double.
    private readonly ITranslationRepository _translationRepository = Substitute.For<ITranslationRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly RecordingProjector _projector = new();

    private UpsertTranslation.Handler CreateHandler(ValueMaybe<IdentityId>? currentUser = null)
        => new(
            new UpsertTranslation.Validator(),
            _translationRepository,
            _unitOfWork,
            new StubCurrentUserAccessor(currentUser ?? ValueMaybe<IdentityId>.From(CurrentUser)),
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
    public async Task Handle_WhenNoCurrentUser_ShouldReturnForbidden()
    {
        // Arrange — defensive guard: the endpoint requires auth, but a token without a parseable
        // subject must never be attributed to an empty submitter.
        UpsertTranslation.Handler handler = CreateHandler(ValueMaybe<IdentityId>.None());

        // Act
        Result<TranslationDetailResponse> result = await handler.Handle(Command(1, "Polski"), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Translations.Unauthenticated");
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
        removed.ProvideTranslation("Polski", CurrentUser, Now);
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

        // Act
        Result<TranslationDetailResponse> result = await CreateHandler().Handle(Command(1, "Witaj"), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Status.ShouldBe(TranslationStatus.Draft);
        result.Value.TranslatedText.ShouldBe("Witaj");
        result.Value.SubmittedById.ShouldBe(CurrentUser.Value);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        // Editing a non-Approved row does not change the distributed set, so no artifact rebuild.
        _projector.RebuildCount.ShouldBe(0);
    }

    [Fact]
    public async Task Handle_OnApprovedRow_ShouldMoveToDraftAndRebuildArtifact()
    {
        // Arrange — an Approved row is in the distributed file; editing pulls it out (spec 0001 Q1).
        Translation approved = Untranslated();
        approved.ProvideTranslation("Stary polski", CurrentUser, Now);
        approved.Approve(CurrentUser, Now);
        GivenStoredRow(approved);

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
        needsReview.ProvideTranslation("Stary polski", CurrentUser, Now);
        needsReview.ApplySourceChange(TranslationSource.Create("New English", null, null).Value, VersionId, Now);
        GivenStoredRow(needsReview);

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

    private sealed class StubCurrentUserAccessor : ICurrentUserAccessor
    {
        public StubCurrentUserAccessor(ValueMaybe<IdentityId> identityId)
        {
            MaybeIdentityId = identityId;
        }

        public ValueMaybe<IdentityId> MaybeIdentityId { get; }
        public string? Email => null;
        public string? Username => null;
        public IEnumerable<string> Roles => [];
        public bool IsAuthenticated => MaybeIdentityId.HasValue;
        public bool IsInRole(string role) => false;
        public bool HasOnlyRegularUserPrivileges() => true;
    }
}
