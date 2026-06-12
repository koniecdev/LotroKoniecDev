using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Enums;

namespace LotroKoniecDev.SharedKernel.Tests.Unit.Tests.BuildingBlocks;

public sealed class ErrorTests
{
    [Fact]
    public void Constructor_WithoutType_ShouldDefaultToFailure()
    {
        // Act
        Error error = new("Test.Code", "Test message");

        // Assert
        error.Type.ShouldBe(TypeOfError.Failure);
    }

    [Fact]
    public void None_ShouldHaveEmptyCodeAndMessage()
    {
        // Act
        Error error = Error.None;

        // Assert
        error.Code.ShouldBe(string.Empty);
        error.Message.ShouldBe(string.Empty);
    }

    [Fact]
    public void Equality_SameValues_ShouldBeEqual()
    {
        // Arrange
        Error first = new("Test.Code", "Test message", TypeOfError.NotFound);
        Error second = new("Test.Code", "Test message", TypeOfError.NotFound);

        // Assert
        first.ShouldBe(second);
        (first == second).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Other.Code", "Test message", TypeOfError.NotFound)]
    [InlineData("Test.Code", "Other message", TypeOfError.NotFound)]
    [InlineData("Test.Code", "Test message", TypeOfError.Validation)]
    public void Equality_DifferentValues_ShouldNotBeEqual(string code, string message, TypeOfError type)
    {
        // Arrange
        Error first = new("Test.Code", "Test message", TypeOfError.NotFound);
        Error second = new(code, message, type);

        // Assert
        first.ShouldNotBe(second);
        (first != second).ShouldBeTrue();
    }

    [Fact]
    public void GetHashCode_SameValues_ShouldBeEqual()
    {
        // Arrange
        Error first = new("Test.Code", "Test message", TypeOfError.NotFound);
        Error second = new("Test.Code", "Test message", TypeOfError.NotFound);

        // Assert
        first.GetHashCode().ShouldBe(second.GetHashCode());
    }

    [Fact]
    public void ToString_ShouldFormatTypeCodeAndMessage()
    {
        // Arrange
        Error error = new("Test.Code", "Test message", TypeOfError.Validation);

        // Act
        string formatted = error.ToString();

        // Assert
        formatted.ShouldBe("[Validation] Test.Code: Test message");
    }
}
