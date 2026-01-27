using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.Tests.Integration.Core;

public class ErrorTests
{
    [Fact]
    public void Error_ShouldStoreCodeAndMessage()
    {
        // Arrange & Act
        var error = new Error("TEST.CODE", "Test message", ErrorType.Validation);

        // Assert
        error.Code.Should().Be("TEST.CODE");
        error.Message.Should().Be("Test message");
        error.Type.Should().Be(ErrorType.Validation);
    }

    [Fact]
    public void Error_ToString_ShouldFormatCorrectly()
    {
        // Arrange
        var error = new Error("TEST.CODE", "Test message", ErrorType.NotFound);

        // Act
        string result = error.ToString();

        // Assert
        result.Should().Be("[NotFound] TEST.CODE: Test message");
    }

    [Fact]
    public void Error_Validation_ShouldCreateValidationError()
    {
        // Act
        var error = Error.Validation("VAL.CODE", "Validation message");

        // Assert
        error.Type.Should().Be(ErrorType.Validation);
        error.Code.Should().Be("VAL.CODE");
        error.Message.Should().Be("Validation message");
    }

    [Fact]
    public void Error_NotFound_ShouldCreateNotFoundError()
    {
        // Act
        var error = Error.NotFound("NF.CODE", "Not found message");

        // Assert
        error.Type.Should().Be(ErrorType.NotFound);
    }

    [Fact]
    public void Error_IoError_ShouldCreateIoError()
    {
        // Act
        var error = Error.IoError("IO.CODE", "IO error message");

        // Assert
        error.Type.Should().Be(ErrorType.IoError);
    }

    [Fact]
    public void Error_Equality_ShouldBeBasedOnValues()
    {
        // Arrange
        var error1 = new Error("CODE", "Message", ErrorType.Failure);
        var error2 = new Error("CODE", "Message", ErrorType.Failure);
        var error3 = new Error("DIFFERENT", "Message", ErrorType.Failure);

        // Assert
        error1.Should().Be(error2);
        error1.Should().NotBe(error3);
        (error1 == error2).Should().BeTrue();
        (error1 != error3).Should().BeTrue();
    }

    [Fact]
    public void Error_GetHashCode_ShouldBeSameForEqualErrors()
    {
        // Arrange
        var error1 = new Error("CODE", "Message", ErrorType.Failure);
        var error2 = new Error("CODE", "Message", ErrorType.Failure);

        // Assert
        error1.GetHashCode().Should().Be(error2.GetHashCode());
    }
}
