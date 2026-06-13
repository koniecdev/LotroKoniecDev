using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Core.Errors;

namespace LotroKoniecDev.TranslationSystem.Domain.Tests.Unit.Tests.Aggregates.GameVersionAggregate;

public sealed class LotroNotationVersionTests
{
    [Fact]
    public void Create_WithValidValue_ShouldSucceed()
    {
        // Act
        Result<LotroNotationVersion> result = LotroNotationVersion.Create("48.0");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.ShouldBe("48.0");
    }

    [Fact]
    public void Create_WithSurroundingWhitespace_ShouldTrimValue()
    {
        // Act
        Result<LotroNotationVersion> result = LotroNotationVersion.Create("  47.1.1  ");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.ShouldBe("47.1.1");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithNullOrWhitespace_ShouldReturnNullOrEmptyError(string? value)
    {
        // Act
        Result<LotroNotationVersion> result = LotroNotationVersion.Create(value!);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(DomainErrors.GameVersionEntity.VersionProperty.NullOrEmpty);
    }

    [Fact]
    public void Create_WithTooLongValue_ShouldReturnTooLongError()
    {
        // Arrange
        string value = new('1', LotroNotationVersion.VersionMaxLength + 1);

        // Act
        Result<LotroNotationVersion> result = LotroNotationVersion.Create(value);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(DomainErrors.GameVersionEntity.VersionProperty.LongerThanAllowed);
    }

    [Fact]
    public void Create_WithValueAtMaxLength_ShouldSucceed()
    {
        // Arrange
        string value = new('1', LotroNotationVersion.VersionMaxLength);

        // Act
        Result<LotroNotationVersion> result = LotroNotationVersion.Create(value);

        // Assert
        result.IsSuccess.ShouldBeTrue();
    }
}
