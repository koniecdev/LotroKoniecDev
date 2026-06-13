using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Core.Errors;

namespace LotroKoniecDev.TranslationSystem.Domain.Tests.Unit.Tests.Aggregates.TranslationAggregate;

public sealed class FragmentKeyTests
{
    [Fact]
    public void Create_WithValidValues_ShouldSucceed()
    {
        // Act
        Result<FragmentKey> result = FragmentKey.Create(620756992, 1001);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.FileId.ShouldBe(620756992);
        result.Value.GossipId.ShouldBe(1001);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_WithNonPositiveFileId_ShouldReturnInvalidFileId(int fileId)
    {
        // Act
        Result<FragmentKey> result = FragmentKey.Create(fileId, 1001);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(DomainErrors.TranslationEntity.FragmentKeyProperty.InvalidFileId);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Create_WithNegativeGossipId_ShouldReturnInvalidGossipId(long gossipId)
    {
        // Act
        Result<FragmentKey> result = FragmentKey.Create(620756992, gossipId);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(DomainErrors.TranslationEntity.FragmentKeyProperty.InvalidGossipId);
    }

    [Fact]
    public void Create_WithZeroGossipId_ShouldSucceed()
    {
        // Act — the patcher applies no lower bound; a zero id must not fail the import.
        Result<FragmentKey> result = FragmentKey.Create(620756992, 0);

        // Assert
        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Equals_WithSameValues_ShouldBeEqual()
    {
        // Arrange
        FragmentKey first = FragmentKey.Create(620756992, 1001).Value;
        FragmentKey second = FragmentKey.Create(620756992, 1001).Value;

        // Assert
        first.ShouldBe(second);
        first.GetHashCode().ShouldBe(second.GetHashCode());
    }

    [Fact]
    public void Equals_WithDifferentValues_ShouldNotBeEqual()
    {
        // Arrange
        FragmentKey first = FragmentKey.Create(620756992, 1001).Value;
        FragmentKey second = FragmentKey.Create(620756992, 1002).Value;

        // Assert
        first.ShouldNotBe(second);
    }
}
