using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Core.Errors;

namespace LotroKoniecDev.TranslationSystem.Domain.Tests.Unit.Tests.Aggregates.TranslationAggregate;

public sealed class TranslationSourceTests
{
    [Fact]
    public void Create_WithTextAndArgs_ShouldSucceed()
    {
        // Act
        Result<TranslationSource> result = TranslationSource.Create("Welcome to <--DO_NOT_TOUCH!--> Middle-earth", "1", "1");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Text.ShouldBe("Welcome to <--DO_NOT_TOUCH!--> Middle-earth");
        result.Value.ArgsOrder.ShouldBe("1");
        result.Value.ArgsId.ShouldBe("1");
    }

    [Fact]
    public void Create_WithNullText_ShouldReturnTextRequired()
    {
        // Act
        Result<TranslationSource> result = TranslationSource.Create(null!, null, null);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(DomainErrors.TranslationEntity.SourceProperty.TextRequired);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NULL")]
    [InlineData("null")]
    public void Create_WithAbsentArgs_ShouldNormalizeToNull(string? argsValue)
    {
        // Act
        Result<TranslationSource> result = TranslationSource.Create("Some text", argsValue, argsValue);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ArgsOrder.ShouldBeNull();
        result.Value.ArgsId.ShouldBeNull();
    }

    [Fact]
    public void Equals_WithSameTextAndArgs_ShouldBeEqual()
    {
        // Arrange
        TranslationSource first = TranslationSource.Create("Text", "1-2", "1-2").Value;
        TranslationSource second = TranslationSource.Create("Text", "1-2", "1-2").Value;

        // Assert
        first.ShouldBe(second);
        first.GetHashCode().ShouldBe(second.GetHashCode());
    }

    [Fact]
    public void Equals_WithSameTextButDifferentArgs_ShouldNotBeEqual()
    {
        // Arrange — args are part of the source: a placeholder-structure change is a source change.
        TranslationSource first = TranslationSource.Create("Text", "1-2", "1-2").Value;
        TranslationSource second = TranslationSource.Create("Text", "2-1", "1-2").Value;

        // Assert
        first.ShouldNotBe(second);
    }

    [Fact]
    public void Equals_WithNullArgsBothSides_ShouldBeEqual()
    {
        // Arrange
        TranslationSource first = TranslationSource.Create("Text", null, null).Value;
        TranslationSource second = TranslationSource.Create("Text", "NULL", "NULL").Value;

        // Assert — NULL and absent collapse to the same value.
        first.ShouldBe(second);
    }
}
