using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Core.Errors;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.Domain.Tests.Unit.Tests.Aggregates.GameVersionAggregate;

public sealed class GameVersionTests
{
    private static readonly DateTimeOffset DetectedAt = new(2026, 6, 12, 12, 0, 0, TimeSpan.Zero);

    private static GameVersion CreateGameVersion(string version = "48.0")
        => GameVersion.Create(version, DetectedAt).Value;

    [Fact]
    public void Create_WithValidVersion_ShouldSucceedAsUnprocessed()
    {
        // Act
        Result<GameVersion> result = GameVersion.Create("48.0", DetectedAt);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Version.ShouldBe("48.0");
        result.Value.DetectedAt.ShouldBe(DetectedAt);
        result.Value.Status.ShouldBe(GameVersionStatus.Unprocessed);
        result.Value.Id.Value.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public void Create_WithSurroundingWhitespace_ShouldTrimVersion()
    {
        // Act
        Result<GameVersion> result = GameVersion.Create("  47.1.1  ", DetectedAt);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Version.ShouldBe("47.1.1");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithNullOrWhitespaceVersion_ShouldReturnVersionRequired(string? version)
    {
        // Act
        Result<GameVersion> result = GameVersion.Create(version!, DetectedAt);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(DomainErrors.GameVersionEntity.VersionProperty.NullOrEmpty);
    }

    [Fact]
    public void Create_WithTooLongVersion_ShouldReturnVersionTooLong()
    {
        // Arrange
        string version = new('1', GameVersion.VersionMaxLength + 1);

        // Act
        Result<GameVersion> result = GameVersion.Create(version, DetectedAt);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(DomainErrors.GameVersionEntity.VersionProperty.LongerThanAllowed);
    }

    [Fact]
    public void Create_WithVersionAtMaxLength_ShouldSucceed()
    {
        // Arrange
        string version = new('1', GameVersion.VersionMaxLength);

        // Act
        Result<GameVersion> result = GameVersion.Create(version, DetectedAt);

        // Assert
        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Create_WithDefaultDetectedAt_ShouldThrowArgumentException()
    {
        // Assert
        Should.Throw<ArgumentException>(() => GameVersion.Create("48.0", default));
    }

    [Fact]
    public void MarkProcessed_WhenUnprocessed_ShouldTransitionToProcessed()
    {
        // Arrange
        GameVersion gameVersion = CreateGameVersion();

        // Act
        Result result = gameVersion.MarkProcessed();

        // Assert
        result.IsSuccess.ShouldBeTrue();
        gameVersion.Status.ShouldBe(GameVersionStatus.Processed);
    }

    [Fact]
    public void MarkProcessed_WhenAlreadyProcessed_ShouldSucceedIdempotently()
    {
        // Arrange
        GameVersion gameVersion = CreateGameVersion();
        gameVersion.MarkProcessed();

        // Act
        Result result = gameVersion.MarkProcessed();

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
        Result result = gameVersion.MarkProcessed();

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
        gameVersion.MarkProcessed();

        // Act
        Result result = gameVersion.MarkSuperseded();

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(DomainErrors.GameVersionEntity.ProcessedCannotBeSuperseded(gameVersion.Id));
        gameVersion.Status.ShouldBe(GameVersionStatus.Processed);
    }
}
