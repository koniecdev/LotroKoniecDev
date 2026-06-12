using LotroKoniecDev.SharedKernel.Guards;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;

namespace LotroKoniecDev.SharedKernel.Tests.Unit.Tests.Guards;

public sealed class EnsureTests
{
    public enum TestEnum
    {
        Unset = 0,
        First = 1,
        Second = 2
    }

    [Fact]
    public void IsValidNonDefaultEnum_WithDefaultValue_ShouldThrowArgumentException()
    {
        // Arrange
        TestEnum value = TestEnum.Unset;

        // Assert
        Should.Throw<ArgumentException>(() => Ensure.IsValidNonDefaultEnum(value));
    }

    [Fact]
    public void IsValidNonDefaultEnum_WithUndefinedValue_ShouldThrowArgumentOutOfRangeException()
    {
        // Arrange
        TestEnum value = (TestEnum)999;

        // Assert
        Should.Throw<ArgumentOutOfRangeException>(() => Ensure.IsValidNonDefaultEnum(value));
    }

    [Theory]
    [InlineData(TestEnum.First)]
    [InlineData(TestEnum.Second)]
    public void IsValidNonDefaultEnum_WithDefinedValue_ShouldNotThrow(TestEnum value)
    {
        // Assert
        Should.NotThrow(() => Ensure.IsValidNonDefaultEnum(value));
    }

    [Fact]
    public void NotEmpty_WithEmptyGuid_ShouldThrowArgumentException()
    {
        // Arrange
        Guid id = Guid.Empty;

        // Assert
        Should.Throw<ArgumentException>(() => Ensure.NotEmpty(id));
    }

    [Fact]
    public void NotEmpty_WithNonEmptyGuid_ShouldNotThrow()
    {
        // Arrange
        Guid id = Guid.NewGuid();

        // Assert
        Should.NotThrow(() => Ensure.NotEmpty(id));
    }

    [Fact]
    public void NotEmpty_WithEmptyStronglyTypedId_ShouldThrowArgumentException()
    {
        // Arrange
        IdentityId id = IdentityId.Create(Guid.Empty);

        // Assert
        Should.Throw<ArgumentException>(() => Ensure.NotEmpty(id));
    }

    [Fact]
    public void NotEmpty_WithNonEmptyStronglyTypedId_ShouldNotThrow()
    {
        // Arrange
        IdentityId id = IdentityId.Create();

        // Assert
        Should.NotThrow(() => Ensure.NotEmpty(id));
    }

    [Fact]
    public void NotEmpty_WithDefaultDateTimeOffset_ShouldThrowArgumentException()
    {
        // Arrange
        DateTimeOffset value = default;

        // Assert
        Should.Throw<ArgumentException>(() => Ensure.NotEmpty(value));
    }

    [Fact]
    public void NotEmpty_WithNonDefaultDateTimeOffset_ShouldNotThrow()
    {
        // Arrange
        DateTimeOffset value = DateTimeOffset.UtcNow;

        // Assert
        Should.NotThrow(() => Ensure.NotEmpty(value));
    }
}
