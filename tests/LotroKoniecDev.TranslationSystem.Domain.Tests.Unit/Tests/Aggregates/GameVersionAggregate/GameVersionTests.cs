using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Core.Errors;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.Domain.Tests.Unit.Tests.Aggregates.GameVersionAggregate;

public sealed class GameVersionTests
{
    private static readonly DateTimeOffset DetectedAt = new(2026, 6, 12, 12, 0, 0, TimeSpan.Zero);

    private static GameVersion CreateGameVersion(string version = "48.0")
        => GameVersion.Create(LotroNotationVersion.Create(version).Value, DetectedAt).Value;

    [Fact]
    public void Create_WithValidVersion_ShouldSucceedAsUnprocessed()
    {
        // Arrange
        LotroNotationVersion version = LotroNotationVersion.Create("47.1.1").Value;

        // Act
        Result<GameVersion> result = GameVersion.Create(version, DetectedAt);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.LotroNotationVersion.Value.ShouldBe("47.1.1");
        result.Value.DetectedAt.ShouldBe(DetectedAt);
        result.Value.Status.ShouldBe(GameVersionStatus.Unprocessed);
        result.Value.Id.Value.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public void Create_WithDefaultDetectedAt_ShouldThrowArgumentException()
    {
        // Arrange
        LotroNotationVersion version = LotroNotationVersion.Create("48.0").Value;

        // Assert
        Should.Throw<ArgumentException>(() => GameVersion.Create(version, default));
    }

    [Fact]
    public void Create_WithNullVersion_ShouldThrowArgumentNullException()
    {
        // Assert
        Should.Throw<ArgumentNullException>(() => GameVersion.Create(null!, DetectedAt));
    }

    [Fact]
    public void MarkProcessed_WhenUnprocessed_ShouldTransitionToProcessed()
    {
        // Arrange
        GameVersion gameVersion = CreateGameVersion();

        // Act
        Result result = gameVersion.MarkAsProcessed();

        // Assert
        result.IsSuccess.ShouldBeTrue();
        gameVersion.Status.ShouldBe(GameVersionStatus.Processed);
    }

    [Fact]
    public void MarkProcessed_WhenAlreadyProcessed_ShouldSucceedIdempotently()
    {
        // Arrange
        GameVersion gameVersion = CreateGameVersion();
        gameVersion.MarkAsProcessed();

        // Act
        Result result = gameVersion.MarkAsProcessed();

        // Assert
        result.IsSuccess.ShouldBeTrue();
        gameVersion.Status.ShouldBe(GameVersionStatus.Processed);
    }

    [Fact]
    public void MarkProcessed_WhenSuperseded_ShouldReturnFailure()
    {
        // Arrange
        GameVersion gameVersion = CreateGameVersion();
        gameVersion.MarkSuperseded();

        // Act
        Result result = gameVersion.MarkAsProcessed();

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(DomainErrors.GameVersionEntity.SupersededCannotBeProcessed(gameVersion.Id));
        gameVersion.Status.ShouldBe(GameVersionStatus.Superseded);
    }

    [Fact]
    public void MarkSuperseded_WhenUnprocessed_ShouldTransitionToSuperseded()
    {
        // Arrange
        GameVersion gameVersion = CreateGameVersion();

        // Act
        Result result = gameVersion.MarkSuperseded();

        // Assert
        result.IsSuccess.ShouldBeTrue();
        gameVersion.Status.ShouldBe(GameVersionStatus.Superseded);
    }

    [Fact]
    public void MarkSuperseded_WhenAlreadySuperseded_ShouldSucceedIdempotently()
    {
        // Arrange
        GameVersion gameVersion = CreateGameVersion();
        gameVersion.MarkSuperseded();

        // Act
        Result result = gameVersion.MarkSuperseded();

        // Assert
        result.IsSuccess.ShouldBeTrue();
        gameVersion.Status.ShouldBe(GameVersionStatus.Superseded);
    }

    [Fact]
    public void MarkSuperseded_WhenProcessed_ShouldReturnFailure()
    {
        // Arrange
        GameVersion gameVersion = CreateGameVersion();
        gameVersion.MarkAsProcessed();

        // Act
        Result result = gameVersion.MarkSuperseded();

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(DomainErrors.GameVersionEntity.ProcessedCannotBeSuperseded(gameVersion.Id));
        gameVersion.Status.ShouldBe(GameVersionStatus.Processed);
    }

    [Fact]
    public void EnsureCanBeDeleted_WhenUnprocessed_ShouldSucceed()
    {
        // Arrange
        GameVersion gameVersion = CreateGameVersion();

        // Act
        Result result = gameVersion.EnsureCanBeDeleted();

        // Assert
        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void EnsureCanBeDeleted_WhenProcessed_ShouldReturnFailure()
    {
        // Arrange
        GameVersion gameVersion = CreateGameVersion();
        gameVersion.MarkAsProcessed();

        // Act
        Result result = gameVersion.EnsureCanBeDeleted();

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(DomainErrors.GameVersionEntity.OnlyUnprocessedCanBeDeleted(gameVersion.Id));
    }

    [Fact]
    public void EnsureCanBeDeleted_WhenSuperseded_ShouldReturnFailure()
    {
        // Arrange
        GameVersion gameVersion = CreateGameVersion();
        gameVersion.MarkSuperseded();

        // Act
        Result result = gameVersion.EnsureCanBeDeleted();

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(DomainErrors.GameVersionEntity.OnlyUnprocessedCanBeDeleted(gameVersion.Id));
    }
}
