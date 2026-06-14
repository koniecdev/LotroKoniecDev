using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.ValueObjects;

namespace LotroKoniecDev.TranslationSystem.Domain.Tests.Unit.Tests.Aggregates.TranslatorAggregate.ValueObjects;

public sealed class DisplayNameTests
{
    [Fact]
    public void Create_WithValidValue_ShouldSucceed()
    {
        // Act
        Result<DisplayName> result = DisplayName.Create("Aragorn");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.ShouldBe("Aragorn");
    }

    [Fact]
    public void Create_ShouldTrimSurroundingWhitespace()
    {
        // Act
        Result<DisplayName> result = DisplayName.Create("  Gandalf  ");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.ShouldBe("Gandalf");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithNullOrWhitespace_ShouldFail(string? value)
    {
        // Act
        Result<DisplayName> result = DisplayName.Create(value!);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("TranslatorEntity.DisplayName.NullOrEmpty");
    }

    [Fact]
    public void Create_AtMaxLength_ShouldSucceed()
    {
        // Arrange
        string atLimit = new('a', DisplayName.MaxLength);

        // Act
        Result<DisplayName> result = DisplayName.Create(atLimit);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.Length.ShouldBe(DisplayName.MaxLength);
    }

    [Fact]
    public void Create_OverMaxLength_ShouldFail()
    {
        // Arrange
        string tooLong = new('a', DisplayName.MaxLength + 1);

        // Act
        Result<DisplayName> result = DisplayName.Create(tooLong);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("TranslatorEntity.DisplayName.LongerThanAllowed");
    }

    [Fact]
    public void Equality_SameValue_ShouldBeEqual()
    {
        // Act
        DisplayName first = DisplayName.Create("Frodo").Value;
        DisplayName second = DisplayName.Create("Frodo").Value;

        // Assert
        first.ShouldBe(second);
        (first == second).ShouldBeTrue();
    }
}
