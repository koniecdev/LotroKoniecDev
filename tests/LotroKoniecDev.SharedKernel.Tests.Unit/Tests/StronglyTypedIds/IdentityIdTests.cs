using System.Text.Json;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;

namespace LotroKoniecDev.SharedKernel.Tests.Unit.Tests.StronglyTypedIds;

public sealed class IdentityIdTests
{
    [Fact]
    public void Create_ShouldGenerateVersion7Guid()
    {
        // Act
        IdentityId id = IdentityId.Create();

        // Assert
        id.Value.ShouldNotBe(Guid.Empty);
        id.Value.Version.ShouldBe(7);
    }

    [Fact]
    public void Create_CalledTwice_ShouldGenerateUniqueIds()
    {
        // Act
        IdentityId first = IdentityId.Create();
        IdentityId second = IdentityId.Create();

        // Assert
        first.ShouldNotBe(second);
    }

    [Fact]
    public void Create_WithGuid_ShouldWrapProvidedGuid()
    {
        // Arrange
        Guid guid = Guid.NewGuid();

        // Act
        IdentityId id = IdentityId.Create(guid);

        // Assert
        id.Value.ShouldBe(guid);
    }

    [Fact]
    public void Equality_SameGuid_ShouldBeEqual()
    {
        // Arrange
        Guid guid = Guid.NewGuid();

        // Act
        IdentityId first = IdentityId.Create(guid);
        IdentityId second = IdentityId.Create(guid);

        // Assert
        first.ShouldBe(second);
        (first == second).ShouldBeTrue();
    }

    [Fact]
    public void JsonSerialization_ShouldRoundTripAsPlainGuid()
    {
        // Arrange
        IdentityId id = IdentityId.Create();

        // Act
        string json = JsonSerializer.Serialize(id);
        IdentityId deserialized = JsonSerializer.Deserialize<IdentityId>(json);

        // Assert
        json.ShouldBe($"\"{id.Value}\"");
        deserialized.ShouldBe(id);
    }
}
