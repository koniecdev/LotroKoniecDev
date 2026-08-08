using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Features.GameVersions;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Domain.Core.Errors;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using NSubstitute;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Features.GameVersions;

public sealed class DeleteGameVersionHandlerTests
{
    private static readonly DateTimeOffset DetectedAt = new(2026, 6, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly IGameVersionRepository _gameVersionRepository = Substitute.For<IGameVersionRepository>();
    private readonly ITranslationRepository _translationRepository = Substitute.For<ITranslationRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private DeleteGameVersion.Handler CreateHandler()
        => new(new DeleteGameVersion.Validator(), _gameVersionRepository, _translationRepository, _unitOfWork);

    private static GameVersion UnprocessedVersion()
        => GameVersion.Create(LotroNotationVersion.Create("48.0").Value, DetectedAt).Value;

    [Fact]
    public async Task Handle_WhenIdEmpty_ShouldReturnValidationErrorAndNotDelete()
    {
        // Act
        Result result = await CreateHandler().Handle(new DeleteGameVersion.Command(GameVersionId.Empty), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("GameVersions.Validation");
        _gameVersionRepository.DidNotReceive().Remove(Arg.Any<GameVersion>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenVersionNotFound_ShouldReturnNotFoundAndNotDelete()
    {
        // Arrange
        GameVersionId id = GameVersionId.Create();
        _gameVersionRepository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(Maybe<GameVersion>.None);

        // Act
        Result result = await CreateHandler().Handle(new DeleteGameVersion.Command(id), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(DomainErrors.GameVersionEntity.NotFound(id));
        _gameVersionRepository.DidNotReceive().Remove(Arg.Any<GameVersion>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenVersionIsProcessed_ShouldReturnConflictAndNotDelete()
    {
        // Arrange — a processed version is woven into the lifecycle and may not be removed.
        GameVersion gameVersion = UnprocessedVersion();
        gameVersion.MarkAsProcessed();
        _gameVersionRepository.GetByIdAsync(gameVersion.Id, Arg.Any<CancellationToken>())
            .Returns(Maybe<GameVersion>.From(gameVersion));

        // Act
        Result result = await CreateHandler().Handle(new DeleteGameVersion.Command(gameVersion.Id), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(DomainErrors.GameVersionEntity.ProcessedCannotBeDeleted(gameVersion.Id));
        // The status guard short-circuits before the cross-aggregate reference check.
        await _translationRepository.DidNotReceive().AnyReferencesGameVersionAsync(Arg.Any<GameVersionId>(), Arg.Any<CancellationToken>());
        _gameVersionRepository.DidNotReceive().Remove(Arg.Any<GameVersion>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenVersionIsReferencedByATranslation_ShouldReturnConflictAndNotDelete()
    {
        // Arrange — an unprocessed version that (defensively) a translation still references.
        GameVersion gameVersion = UnprocessedVersion();
        _gameVersionRepository.GetByIdAsync(gameVersion.Id, Arg.Any<CancellationToken>())
            .Returns(Maybe<GameVersion>.From(gameVersion));
        _translationRepository.AnyReferencesGameVersionAsync(gameVersion.Id, Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        Result result = await CreateHandler().Handle(new DeleteGameVersion.Command(gameVersion.Id), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(DomainErrors.GameVersionEntity.CannotDeleteReferencedVersion(gameVersion.Id));
        _gameVersionRepository.DidNotReceive().Remove(Arg.Any<GameVersion>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenUnprocessedAndUnreferenced_ShouldRemoveAndPersist()
    {
        // Arrange — repository reports no referencing translation (default substitute returns false).
        GameVersion gameVersion = UnprocessedVersion();
        _gameVersionRepository.GetByIdAsync(gameVersion.Id, Arg.Any<CancellationToken>())
            .Returns(Maybe<GameVersion>.From(gameVersion));

        // Act
        Result result = await CreateHandler().Handle(new DeleteGameVersion.Command(gameVersion.Id), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        _gameVersionRepository.Received(1).Remove(gameVersion);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenSupersededAndUnreferenced_ShouldRemoveAndPersist()
    {
        // Arrange — a version that was registered and then skipped was never imported against, so
        // retiring it is how the admin frees its version number again (#624).
        GameVersion gameVersion = UnprocessedVersion();
        gameVersion.MarkSuperseded();
        _gameVersionRepository.GetByIdAsync(gameVersion.Id, Arg.Any<CancellationToken>())
            .Returns(Maybe<GameVersion>.From(gameVersion));

        // Act
        Result result = await CreateHandler().Handle(new DeleteGameVersion.Command(gameVersion.Id), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        _gameVersionRepository.Received(1).Remove(gameVersion);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenSupersededButReferencedByATranslation_ShouldReturnConflictAndNotDelete()
    {
        // Arrange — the cross-aggregate net still stands whatever the status (#624 leaves it untouched).
        GameVersion gameVersion = UnprocessedVersion();
        gameVersion.MarkSuperseded();
        _gameVersionRepository.GetByIdAsync(gameVersion.Id, Arg.Any<CancellationToken>())
            .Returns(Maybe<GameVersion>.From(gameVersion));
        _translationRepository.AnyReferencesGameVersionAsync(gameVersion.Id, Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        Result result = await CreateHandler().Handle(new DeleteGameVersion.Command(gameVersion.Id), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(DomainErrors.GameVersionEntity.CannotDeleteReferencedVersion(gameVersion.Id));
        _gameVersionRepository.DidNotReceive().Remove(Arg.Any<GameVersion>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
