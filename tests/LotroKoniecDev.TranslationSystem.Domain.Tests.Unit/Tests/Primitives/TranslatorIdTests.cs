using System.Text.Json;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

namespace LotroKoniecDev.TranslationSystem.Domain.Tests.Unit.Tests.Primitives;

public sealed class TranslatorIdTests
{
    [Fact]
    public void Create_ShouldGenerateVersion7Guid()
    {
        // Act
        TranslatorId id = TranslatorId.Create();

        // Assert
        id.Value.ShouldNotBe(Guid.Empty);
        id.Value.Version.ShouldBe(7);
    }

    [Fact]
    public void Create_WithEmptyGuid_ShouldThrowArgumentException()
    {
        // Assert
        Should.Throw<ArgumentException>(() => TranslatorId.Create(Guid.Empty));
    }

    [Fact]
    public void JsonSerialization_ShouldRoundTripAsPlainGuid()
    {
        // Arrange
        TranslatorId id = TranslatorId.Create();

        // Act
        string json = JsonSerializer.Serialize(id);
        TranslatorId deserialized = JsonSerializer.Deserialize<TranslatorId>(json);

        // Assert
        json.ShouldBe($"\"{id.Value}\"");
        deserialized.ShouldBe(id);
    }
}
