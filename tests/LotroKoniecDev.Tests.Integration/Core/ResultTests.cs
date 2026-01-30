using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Primitives.Enums;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Tests.Integration.Core;

public class ResultTests
{
    [Fact]
    public void Success_ShouldCreateSuccessfulResult()
    {
        // Act
        Result result = Result.Success();

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.IsFailure.Should().BeFalse();
        result.Error.Should().Be(Error.None);
    }

    [Fact]
    public void Success_WithValue_ShouldContainValue()
    {
        // Arrange
        const string expectedValue = "test value";

        // Act
        Result<string> result = Result.Success(expectedValue);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(expectedValue);
    }

    [Fact]
    public void Failure_ShouldCreateFailedResult()
    {
        // Arrange
        Error error = new Error("TEST.ERROR", "Test error message", ErrorType.Failure);

        // Act
        Result result = Result.Failure(error);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(error);
    }

    [Fact]
    public void Failure_WithGenericType_ShouldCreateFailedResult()
    {
        // Arrange
        Error error = new Error("TEST.ERROR", "Test error message");

        // Act
        Result<string> result = Result.Failure<string>(error);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(error);
    }

    [Fact]
    public void Value_OnFailure_ShouldThrowException()
    {
        // Arrange
        Error error = new Error("TEST.ERROR", "Test error message");
        Result<string> result = Result.Failure<string>(error);

        // Act & Assert
        Action act = () => _ = result.Value;
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*failure result*");
    }

    [Fact]
    public void ImplicitConversion_ShouldCreateSuccessResult()
    {
        // Arrange
        const int value = 42;

        // Act
        Result<int> result = value;

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
    }

    [Fact]
    public void ErrorNone_ShouldBeSameInstance()
    {
        // Act
        Error error1 = Error.None;
        Error error2 = Error.None;

        // Assert
        ReferenceEquals(error1, error2).Should().BeTrue();
    }
}
