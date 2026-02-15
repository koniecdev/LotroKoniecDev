using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.Tests.Unit.Tests.Core.BuildingBlocks;

public sealed class ErrorTests
{
    [Fact]
    public void Constructor_ShouldSetPropertiesCorrectly()
    {
        // Act
        Error error = new("Test.Code", "Test message", ErrorType.Validation);

        // Assert
        error.Code.ShouldBe("Test.Code");
        error.Message.ShouldBe("Test message");
        error.Type.ShouldBe(ErrorType.Validation);
    }

    [Fact]
    public void Constructor_WithDefaultType_ShouldUseFailure()
    {
        // Act
        Error error = new("Test.Code", "Test message");

        // Assert
        error.Type.ShouldBe(ErrorType.Failure);
    }

    [Fact]
    public void None_ShouldHaveEmptyCodeAndMessage()
    {
        // Assert
        Error.None.Code.ShouldBeEmpty();
        Error.None.Message.ShouldBeEmpty();
    }

    [Fact]
    public void ToString_ShouldReturnFormattedString()
    {
        // Arrange
        Error error = new("Test.Code", "Test message", ErrorType.Validation);

        // Act
        string result = error.ToString();

        // Assert
        result.ShouldBe("[Validation] Test.Code: Test message");
    }

    [Fact]
    public void Validation_FactoryMethod_ShouldCreateValidationError()
    {
        // Act
        Error error = Error.Validation("Val.Code", "Validation message");

        // Assert
        error.Code.ShouldBe("Val.Code");
        error.Message.ShouldBe("Validation message");
        error.Type.ShouldBe(ErrorType.Validation);
    }

    [Fact]
    public void NotFound_FactoryMethod_ShouldCreateNotFoundError()
    {
        // Act
        Error error = Error.NotFound("NotFound.Code", "Not found message");

        // Assert
        error.Code.ShouldBe("NotFound.Code");
        error.Message.ShouldBe("Not found message");
        error.Type.ShouldBe(ErrorType.NotFound);
    }

    [Fact]
    public void Failure_FactoryMethod_ShouldCreateFailureError()
    {
        // Act
        Error error = Error.Failure("Fail.Code", "Failure message");

        // Assert
        error.Code.ShouldBe("Fail.Code");
        error.Message.ShouldBe("Failure message");
        error.Type.ShouldBe(ErrorType.Failure);
    }

    [Fact]
    public void IoError_FactoryMethod_ShouldCreateIoError()
    {
        // Act
        Error error = Error.IoError("IO.Code", "IO error message");

        // Assert
        error.Code.ShouldBe("IO.Code");
        error.Message.ShouldBe("IO error message");
        error.Type.ShouldBe(ErrorType.IoError);
    }

    [Fact]
    public void Equality_SameValues_ShouldBeEqual()
    {
        // Arrange
        Error error1 = new("Test.Code", "Test message");
        Error error2 = new("Test.Code", "Test message");

        // Assert
        error1.ShouldBe(error2);
        (error1 == error2).ShouldBeTrue();
    }

    [Fact]
    public void Equality_DifferentValues_ShouldNotBeEqual()
    {
        // Arrange
        Error error1 = new("Test.Code1", "Test message");
        Error error2 = new("Test.Code2", "Test message");

        // Assert
        error1.ShouldNotBe(error2);
        (error1 != error2).ShouldBeTrue();
    }
}
