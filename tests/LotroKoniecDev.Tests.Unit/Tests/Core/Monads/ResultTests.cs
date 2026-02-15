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
        result.IsSuccess.ShouldBeTrue();
        result.IsFailure.ShouldBeFalse();
        result.Error.ShouldBe(Error.None);
    }

    [Fact]
    public void Failure_ShouldCreateFailedResult()
    {
        // Arrange
        Error error = new("Test.Error", "Test error message");

        // Act
        Result result = Result.Failure(error);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(error);
    }

    [Fact]
    public void SuccessWithValue_ShouldCreateSuccessfulResultWithValue()
    {
        // Arrange
        const string value = "test value";

        // Act
        Result<string> result = Result.Success(value);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(value);
    }

    [Fact]
    public void FailureWithValue_ShouldCreateFailedResultWithError()
    {
        // Arrange
        Error error = new("Test.Error", "Test error message");

        // Act
        Result<string> result = Result.Failure<string>(error);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(error);
    }

    [Fact]
    public void Value_WhenAccessingOnFailure_ShouldThrowInvalidOperationException()
    {
        // Arrange
        Error error = new("Test.Error", "Test error message");
        Result<string> result = Result.Failure<string>(error);

        // Act
        Action action = () => { _ = result.Value; };

        // Assert
        InvalidOperationException ex = action.ShouldThrow<InvalidOperationException>();
        ex.Message.ShouldContain("failure result");
    }

    [Fact]
    public void ImplicitConversion_FromValueToResult_ShouldCreateSuccessfulResult()
    {
        // Arrange
        const int value = 42;

        // Act
        Result<int> result = value;

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(42);
    }

    [Fact]
    public void Failure_WithErrorNone_ShouldThrowInvalidOperationException()
    {
        // Act & Assert
        Func<Result> action = () => Result.Failure(Error.None);
        action.ShouldThrow<InvalidOperationException>();
    }
}
