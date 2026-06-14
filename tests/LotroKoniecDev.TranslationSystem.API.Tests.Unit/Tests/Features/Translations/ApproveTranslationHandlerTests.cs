using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.API.Auth.CurrentUserAccessing;
using LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;
using LotroKoniecDev.TranslationSystem.API.Features.Translations;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using NSubstitute;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Features.Translations;

public sealed class ApproveTranslationHandlerTests
{
    private const int FileId = 620756992;
    private static readonly DateTimeOffset Now = new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly GameVersionId VersionId = GameVersionId.Create();
    private static readonly IdentityId Submitter = IdentityId.Create();
    private static readonly IdentityId Approver = IdentityId.Create();

    // ITranslationRepository / IUnitOfWork are genuine public boundaries (stubbed); the current-user
    // accessor and the artifact builder are internal interfaces NSubstitute can't proxy, so each gets
    // a focused hand-written double.
    private readonly ITranslationRepository _translationRepository = Substitute.For<ITranslationRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly RecordingArtifactBuilder _artifactBuilder = new();

    private ApproveTranslation.Handler CreateHandler(ValueMaybe<IdentityId>? currentUser = null)
        => new(
            new ApproveTranslation.Validator(),
            _translationRepository,
            _unitOfWork,
            new StubCurrentUserAccessor(currentUser ?? ValueMaybe<IdentityId>.From(Approver)),
            TimeProvider.System,
            _artifactBuilder);

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
        _artifactBuilder.RebuildCount.ShouldBe(0);
    }

    [Fact]
    public async Task Handle_WhenNoCurrentUser_ShouldReturnForbidden()
    {
        // Arrange — defensive guard: the endpoint requires the admin role, but a token without a
        // parseable subject must never be attributed to an empty approver.
        Translation row = Untranslated();
        row.ProvideTranslation("Polski", Submitter, Now);
        GivenStoredRow(row);

        // Act
        Result result = await CreateHandler(ValueMaybe<IdentityId>.None())
            .Handle(new ApproveTranslation.Command(row.Id), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Translations.Unauthenticated");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        _artifactBuilder.RebuildCount.ShouldBe(0);
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
        _artifactBuilder.RebuildCount.ShouldBe(0);
    }

    [Fact]
    public async Task Handle_WhenRowHasNoTranslation_ShouldReturnConflictAndNotPersist()
    {
        // Arrange — an untranslated row has no Polish to publish (spec 0001).
        Translation row = Untranslated();
        GivenStoredRow(row);

        // Act
        Result result = await CreateHandler().Handle(new ApproveTranslation.Command(row.Id), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("TranslationEntity.CannotApproveWithoutTranslation");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        _artifactBuilder.RebuildCount.ShouldBe(0);
    }

    [Fact]
    public async Task Handle_WhenRowRemoved_ShouldReturnConflictAndNotPersist()
    {
        // Arrange — a soft-removed row is excluded from the distributed file (spec 0001).
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
        _artifactBuilder.RebuildCount.ShouldBe(0);
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
        // The row enters the distributed set, so the artifact is always rebuilt on a successful approve.
        _artifactBuilder.RebuildCount.ShouldBe(1);
    }

    [Fact]
    public async Task Handle_OnNeedsReviewRow_ShouldClearInvalidationAndRebuild()
    {
        // Arrange — a re-translated invalidated row: approving resolves the invalidation (spec 0001).
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
        _artifactBuilder.RebuildCount.ShouldBe(1);
    }

    private sealed class RecordingArtifactBuilder : ITranslationArtifactBuilder
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
