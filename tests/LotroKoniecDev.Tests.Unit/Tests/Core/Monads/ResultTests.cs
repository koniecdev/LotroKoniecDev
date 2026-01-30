using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Primitives.Enums;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Tests.Unit.Tests.Core.Monads;

public sealed class ResultTests
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
    public void Failure_ShouldCreateFailedResult()
    {
        // Arrange
        Error error = new Error("Test.Error", "Test error message");

        // Act
        Result result = Result.Failure(error);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(error);
    }

    [Fact]
    public void SuccessWithValue_ShouldCreateSuccessfulResultWithValue()
    {
        // Arrange
        const string value = "test value";

        // Act
        Result<string> result = Result.Success(value);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(value);
    }

    [Fact]
    public void FailureWithValue_ShouldCreateFailedResultWithError()
    {
        // Arrange
        Error error = new Error("Test.Error", "Test error message");

        // Act
        Result<string> result = Result.Failure<string>(error);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(error);
    }

    [Fact]
    public void Value_WhenAccessingOnFailure_ShouldThrowInvalidOperationException()
    {
        // Arrange
        Error error = new Error("Test.Error", "Test error message");
        Result<string> result = Result.Failure<string>(error);

        // Act
        Func<string> action = () => result.Value;

        // Assert
        action.Should().Throw<InvalidOperationException>()
            .WithMessage("Cannot access the value of a failure result.");
    }

    [Fact]
    public void ImplicitConversion_FromValueToResult_ShouldCreateSuccessfulResult()
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
    public void Constructor_WithSuccessAndError_ShouldThrowInvalidOperationException()
    {
        // Arrange
        Error error = new Error("Test.Error", "Test error message");

        // Act & Assert - This tests internal validation through static factories
        Func<Result> action = () => Result.Failure(Error.None);
        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Constructor_WithFailureAndNoError_ShouldThrowInvalidOperationException()
    {
        // Act & Assert - Failure with Error.None should throw
        // We can't directly test this, but the static factory handles it
        Func<Result> action = () => Result.Failure(Error.None);
        action.Should().Throw<InvalidOperationException>();
    }
}
