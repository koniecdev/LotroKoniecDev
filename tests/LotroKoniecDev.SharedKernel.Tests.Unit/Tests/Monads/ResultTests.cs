using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Enums;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.SharedKernel.Tests.Unit.Tests.Monads;

public sealed class ResultTests
{
    [Fact]
    public void Success_ShouldBeSuccessWithNoneError()
    {
        // Act
        Result result = Result.Success();

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.IsFailure.ShouldBeFalse();
        result.Error.ShouldBe(Error.None);
    }

    [Fact]
    public void Failure_ShouldBeFailureExposingError()
    {
        // Arrange
        Error error = new("Test.Code", "Test message", TypeOfError.NotFound);

        // Act
        Result result = Result.Failure(error);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(error);
    }

    [Fact]
    public void SuccessGeneric_ShouldExposeValue()
    {
        // Act
        Result<string> result = Result.Success("value");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("value");
    }

    [Fact]
    public void FailureGeneric_AccessingValue_ShouldThrowInvalidOperationException()
    {
        // Arrange
        Error error = new("Test.Code", "Test message");
        Result<string> result = Result.Failure<string>(error);

        // Assert
        result.IsFailure.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => _ = result.Value);
    }

    [Fact]
    public void ImplicitOperator_FromValue_ShouldCreateSuccessResult()
    {
        // Act
        Result<int> result = 42;

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(42);
    }

    [Fact]
    public void FailureGeneric_ShouldExposeError()
    {
        // Arrange
        Error error = new("Test.Code", "Test message", TypeOfError.Validation);

        // Act
        Result<int> result = Result.Failure<int>(error);

        // Assert
        result.Error.ShouldBe(error);
        result.Error.Type.ShouldBe(TypeOfError.Validation);
    }
}
