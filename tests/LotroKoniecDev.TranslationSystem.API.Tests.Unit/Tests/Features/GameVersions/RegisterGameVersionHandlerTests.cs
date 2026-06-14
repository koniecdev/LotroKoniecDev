using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Features.GameVersions;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;
using NSubstitute;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Features.GameVersions;

public sealed class RegisterGameVersionHandlerTests
{
    // ITranslationRepository-style genuine boundaries; both are public interfaces NSubstitute proxies.
    private readonly IGameVersionRepository _gameVersionRepository = Substitute.For<IGameVersionRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private RegisterGameVersion.Handler CreateHandler()
        => new(new RegisterGameVersion.Validator(), _gameVersionRepository, _unitOfWork, TimeProvider.System);

    [Fact]
    public async Task Handle_WhenVersionEmpty_ShouldReturnValidationError()
    {
        // Act
        Result<GameVersionResponse> result = await CreateHandler().Handle(new RegisterGameVersion.Command("  "), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("GameVersions.Validation");
        _gameVersionRepository.DidNotReceive().Insert(Arg.Any<GameVersion>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenVersionTooLong_ShouldReturnValidationError()
    {
        // Arrange — past the LotroNotationVersion max length.
        string tooLong = new('9', LotroNotationVersion.VersionMaxLength + 1);

        // Act
        Result<GameVersionResponse> result = await CreateHandler().Handle(new RegisterGameVersion.Command(tooLong), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("GameVersions.Validation");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenVersionAlreadyRegistered_ShouldReturnConflictAndNotPersist()
    {
        // Arrange
        _gameVersionRepository.ExistsByVersionAsync(Arg.Any<LotroNotationVersion>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        Result<GameVersionResponse> result = await CreateHandler().Handle(new RegisterGameVersion.Command("48.0"), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("GameVersionEntity.LotroNotationVersion.AlreadyTaken");
        _gameVersionRepository.DidNotReceive().Insert(Arg.Any<GameVersion>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenNew_ShouldCreateUnprocessedAndPersist()
    {
        // Arrange — repository reports the version is new (default substitute returns false).

        // Act
        Result<GameVersionResponse> result = await CreateHandler().Handle(new RegisterGameVersion.Command(" 48.0 "), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Version.ShouldBe("48.0");
        result.Value.Status.ShouldBe(GameVersionStatus.Unprocessed);
        // Persistence is invisible in the returned response (built from the in-memory aggregate),
        // so SaveChanges is the persistence proof — matching the sibling command tests.
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
