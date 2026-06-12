using System.Text.Json;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;

namespace LotroKoniecDev.TranslationSystem.Domain.Tests.Unit.Tests.Primitives;

public sealed class GameVersionIdTests
{
    [Fact]
    public void Create_ShouldGenerateVersion7Guid()
    {
        // Act
        GameVersionId id = GameVersionId.Create();

        // Assert
        id.Value.ShouldNotBe(Guid.Empty);
        id.Value.Version.ShouldBe(7);
    }

    [Fact]
    public void Create_CalledTwice_ShouldGenerateUniqueIds()
    {
        // Act
        GameVersionId first = GameVersionId.Create();
        GameVersionId second = GameVersionId.Create();

        // Assert
        first.ShouldNotBe(second);
    }

    [Fact]
    public void Create_WithGuid_ShouldWrapProvidedGuid()
    {
        // Arrange
        Guid guid = Guid.NewGuid();

        // Act
        GameVersionId id = GameVersionId.Create(guid);

        // Assert
        id.Value.ShouldBe(guid);
    }

    [Fact]
    public void Create_WithEmptyGuid_ShouldThrowArgumentException()
    {
        // Assert
        Should.Throw<ArgumentException>(() => GameVersionId.Create(Guid.Empty));
    }

    [Fact]
    public void Equality_SameGuid_ShouldBeEqual()
    {
        // Arrange
        Guid guid = Guid.NewGuid();

        // Act
        GameVersionId first = GameVersionId.Create(guid);
        GameVersionId second = GameVersionId.Create(guid);

        // Assert
        first.ShouldBe(second);
        (first == second).ShouldBeTrue();
    }

    [Fact]
    public void JsonSerialization_ShouldRoundTripAsPlainGuid()
    {
        // Arrange
        GameVersionId id = GameVersionId.Create();

        // Act
        string json = JsonSerializer.Serialize(id);
        GameVersionId deserialized = JsonSerializer.Deserialize<GameVersionId>(json);

        // Assert
        json.ShouldBe($"\"{id.Value}\"");
        deserialized.ShouldBe(id);
    }
}
