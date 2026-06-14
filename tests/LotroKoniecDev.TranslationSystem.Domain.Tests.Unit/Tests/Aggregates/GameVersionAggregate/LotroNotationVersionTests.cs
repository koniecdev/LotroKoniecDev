using LotroKoniecDev.SharedKernel.Enums;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Core.Errors;

namespace LotroKoniecDev.TranslationSystem.Domain.Tests.Unit.Tests.Aggregates.GameVersionAggregate;

public sealed class LotroNotationVersionTests
{
    [Fact]
    public void Create_WithCanonicalValue_ShouldSucceedAndRoundTrip()
    {
        // Act
        Result<LotroNotationVersion> result = LotroNotationVersion.Create("47.1.1");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.ShouldBe("47.1.1");
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
    [InlineData("48", "48")]
    [InlineData("48.0", "48")]
    [InlineData("48.0.0", "48")]
    [InlineData("47.1", "47.1")]
    [InlineData("47.1.0", "47.1")]
    [InlineData("47.1.0.0", "47.1")]
    [InlineData("47.1.1", "47.1.1")]
    [InlineData("47.0.1", "47.0.1")]
    [InlineData("0", "0")]
    [InlineData("0.0", "0")]
    [InlineData("0.0.0", "0")]
    [InlineData("0.0.0.0", "0")]
    [InlineData("047", "47")]
    [InlineData("48.01", "48.1")]
    [InlineData("4.8.15.16", "4.8.15.16")]
    public void Create_WithVariousNotations_ShouldNormalizeToCanonicalValue(string input, string expectedCanonical)
    {
        // Act
        Result<LotroNotationVersion> result = LotroNotationVersion.Create(input);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.ShouldBe(expectedCanonical);
    }

    [Theory]
    [InlineData("48", "48.0")]
    [InlineData("48", "48.0.0")]
    [InlineData("48.0", "48.0.0")]
    [InlineData("47.1", "47.1.0")]
    [InlineData("47.1", "47.1.0.0")]
    public void Create_WithTrailingZeroEquivalents_ShouldProduceEqualValueObjects(string left, string right)
    {
        // Act
        LotroNotationVersion first = LotroNotationVersion.Create(left).Value;
        LotroNotationVersion second = LotroNotationVersion.Create(right).Value;

        // Assert
        (first == second).ShouldBeTrue();
        first.Equals(second).ShouldBeTrue();
        first.Value.ShouldBe(second.Value);
        first.GetHashCode().ShouldBe(second.GetHashCode());
    }

    [Theory]
    [InlineData("48", "47")]
    [InlineData("48.1", "48.2")]
    [InlineData("47.0.1", "47.1")]
    [InlineData("48.0.1", "48")]
    public void Create_WithDifferentVersions_ShouldProduceUnequalValueObjects(string left, string right)
    {
        // Act
        LotroNotationVersion first = LotroNotationVersion.Create(left).Value;
        LotroNotationVersion second = LotroNotationVersion.Create(right).Value;

        // Assert
        (first == second).ShouldBeFalse();
        first.Equals(second).ShouldBeFalse();
        first.Value.ShouldNotBe(second.Value);
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
    public void Create_WithSingleSegmentAtMaxLength_ShouldSucceed()
    {
        // Arrange
        string value = new('1', LotroNotationVersion.VersionMaxLength);

        // Act
        Result<LotroNotationVersion> result = LotroNotationVersion.Create(value);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.ShouldBe(value);
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("48.x")]
    [InlineData("v48")]
    [InlineData("48-0")]
    [InlineData("48,0")]
    [InlineData("48..0")]
    [InlineData(".48")]
    [InlineData("48.")]
    [InlineData(".")]
    [InlineData("48.0.")]
    [InlineData("48 0")]
    [InlineData("48.0 beta")]
    public void Create_WithInvalidFormat_ShouldReturnInvalidFormatValidationError(string value)
    {
        // Act
        Result<LotroNotationVersion> result = LotroNotationVersion.Create(value);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(DomainErrors.GameVersionEntity.VersionProperty.InvalidFormat);
        result.Error.Type.ShouldBe(TypeOfError.Validation);
    }
}
