using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.Tests.Unit.Tests.Core.BuildingBlocks;

public sealed class ErrorTests
{
    [Fact]
    public void Constructor_ShouldSetPropertiesCorrectly()
    {
        // Act
        var error = new Error("Test.Code", "Test message", ErrorType.Validation);

        // Assert
        error.Code.Should().Be("Test.Code");
        error.Message.Should().Be("Test message");
        error.Type.Should().Be(ErrorType.Validation);
    }

    [Fact]
    public void Constructor_WithDefaultType_ShouldUseFailure()
    {
        // Act
        var error = new Error("Test.Code", "Test message");

        // Assert
        error.Type.Should().Be(ErrorType.Failure);
    }

    [Fact]
    public void None_ShouldHaveEmptyCodeAndMessage()
    {
        // Assert
        Error.None.Code.Should().BeEmpty();
        Error.None.Message.Should().BeEmpty();
    }

    [Fact]
    public void ToString_ShouldReturnFormattedString()
    {
        // Arrange
        var error = new Error("Test.Code", "Test message", ErrorType.Validation);

        // Act
        var result = error.ToString();

        // Assert
        result.Should().Be("[Validation] Test.Code: Test message");
    }

    [Fact]
    public void Validation_FactoryMethod_ShouldCreateValidationError()
    {
        // Act
        var error = Error.Validation("Val.Code", "Validation message");

        // Assert
        error.Code.Should().Be("Val.Code");
        error.Message.Should().Be("Validation message");
        error.Type.Should().Be(ErrorType.Validation);
    }

    [Fact]
    public void NotFound_FactoryMethod_ShouldCreateNotFoundError()
    {
        // Act
        var error = Error.NotFound("NotFound.Code", "Not found message");

        // Assert
        error.Code.Should().Be("NotFound.Code");
        error.Message.Should().Be("Not found message");
        error.Type.Should().Be(ErrorType.NotFound);
    }

    [Fact]
    public void Failure_FactoryMethod_ShouldCreateFailureError()
    {
        // Act
        var error = Error.Failure("Fail.Code", "Failure message");

        // Assert
        error.Code.Should().Be("Fail.Code");
        error.Message.Should().Be("Failure message");
        error.Type.Should().Be(ErrorType.Failure);
    }

    [Fact]
    public void IoError_FactoryMethod_ShouldCreateIoError()
    {
        // Act
        var error = Error.IoError("IO.Code", "IO error message");

        // Assert
        error.Code.Should().Be("IO.Code");
        error.Message.Should().Be("IO error message");
        error.Type.Should().Be(ErrorType.IoError);
    }

    [Fact]
    public void Equality_SameValues_ShouldBeEqual()
    {
        // Arrange
        var error1 = new Error("Test.Code", "Test message", ErrorType.Failure);
        var error2 = new Error("Test.Code", "Test message", ErrorType.Failure);

        // Assert
        error1.Should().Be(error2);
        (error1 == error2).Should().BeTrue();
    }

    [Fact]
    public void Equality_DifferentValues_ShouldNotBeEqual()
    {
        // Arrange
        var error1 = new Error("Test.Code1", "Test message", ErrorType.Failure);
        var error2 = new Error("Test.Code2", "Test message", ErrorType.Failure);

        // Assert
        error1.Should().NotBe(error2);
        (error1 != error2).Should().BeTrue();
    }
}
