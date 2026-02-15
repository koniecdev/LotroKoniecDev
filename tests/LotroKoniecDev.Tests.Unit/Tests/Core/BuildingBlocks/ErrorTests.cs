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

    [Theory]
    [MemberData(nameof(FactoryMethodTestCases))]
    public void FactoryMethod_ShouldCreateErrorWithCorrectType(
        Func<string, string, Error> factory, ErrorType expectedType)
    {
        // Act
        Error error = factory("Test.Code", "Test message");

        // Assert
        error.Code.ShouldBe("Test.Code");
        error.Message.ShouldBe("Test message");
        error.Type.ShouldBe(expectedType);
    }

    public static TheoryData<Func<string, string, Error>, ErrorType> FactoryMethodTestCases => new()
    {
        { Error.Validation, ErrorType.Validation },
        { Error.NotFound, ErrorType.NotFound },
        { Error.Failure, ErrorType.Failure },
        { Error.IoError, ErrorType.IoError },
    };

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
