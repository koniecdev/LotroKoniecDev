using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.ValueObjects;

namespace LotroKoniecDev.TranslationSystem.Domain.Tests.Unit.Tests.Aggregates.TranslatorAggregate.ValueObjects;

public sealed class EmailTests
{
    [Theory]
    [InlineData("translator@lotro-translator.pl")]
    [InlineData("a.b+tag@sub.example.co")]
    public void Create_WithValidValue_ShouldSucceed(string value)
    {
        // Act
        Result<Email> result = Email.Create(value);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.ShouldBe(value);
    }

    [Fact]
    public void Create_ShouldTrimSurroundingWhitespace()
    {
        // Act
        Result<Email> result = Email.Create("  user@example.com  ");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.ShouldBe("user@example.com");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("missing@domain")]
    [InlineData("@no-local.com")]
    [InlineData("two@@ats.com")]
    public void Create_WithInvalidValue_ShouldFailWithInvalidFormat(string? value)
    {
        // Act
        Result<Email> result = Email.Create(value!);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("TranslatorEntity.Email.InvalidFormat");
    }

    [Fact]
    public void Create_OverMaxLength_ShouldFail()
    {
        // Arrange — a syntactically plausible but over-long address.
        string local = new('a', Email.MaxLength);
        string tooLong = $"{local}@example.com";

        // Act
        Result<Email> result = Email.Create(tooLong);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("TranslatorEntity.Email.LongerThanAllowed");
    }

    [Fact]
    public void Equality_SameValue_ShouldBeEqual()
    {
        // Act
        Email first = Email.Create("user@example.com").Value;
        Email second = Email.Create("user@example.com").Value;

        // Assert
        first.ShouldBe(second);
        (first == second).ShouldBeTrue();
    }
}
