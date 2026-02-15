using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Domain.Core.Extensions;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Tests.Unit.Tests.Extensions;

public sealed class ResultExtensionsTests
{
    [Fact]
    public void OnSuccess_WhenSuccess_ShouldExecuteAction()
    {
        // Arrange
        Result<string> result = Result.Success("test");
        bool wasExecuted = false;
        string? capturedValue = null;

        // Act
        result.OnSuccess(v =>
        {
            wasExecuted = true;
            capturedValue = v;
        });

        // Assert
        wasExecuted.ShouldBeTrue();
        capturedValue.ShouldBe("test");
    }

    [Fact]
    public void OnSuccess_WhenFailure_ShouldNotExecuteAction()
    {
        // Arrange
        Error error = new("Test.Error", "Test message");
        Result<string> result = Result.Failure<string>(error);
        bool wasExecuted = false;

        // Act
        result.OnSuccess(_ => wasExecuted = true);

        // Assert
        wasExecuted.ShouldBeFalse();
    }

    [Fact]
    public void OnSuccess_ShouldReturnSameResult()
    {
        // Arrange
        Result<string> result = Result.Success("test");

        // Act
        Result<string> returned = result.OnSuccess(_ => { });

        // Assert
        returned.ShouldBeSameAs(result);
    }

    [Fact]
    public void OnFailure_WhenFailure_ShouldExecuteAction()
    {
        // Arrange
        Error error = new("Test.Error", "Test message");
        Result<string> result = Result.Failure<string>(error);
        bool wasExecuted = false;
        Error? capturedError = null;

        // Act
        result.OnFailure(e =>
        {
            wasExecuted = true;
            capturedError = e;
        });

        // Assert
        wasExecuted.ShouldBeTrue();
        capturedError.ShouldBe(error);
    }

    [Fact]
    public void OnFailure_WhenSuccess_ShouldNotExecuteAction()
    {
        // Arrange
        Result<string> result = Result.Success("test");
        bool wasExecuted = false;

        // Act
        result.OnFailure(_ => wasExecuted = true);

        // Assert
        wasExecuted.ShouldBeFalse();
    }

    [Fact]
    public void Map_WhenSuccess_ShouldTransformValue()
    {
        // Arrange
        Result<int> result = Result.Success(5);

        // Act
        Result<int> mapped = result.Map(v => v * 2);

        // Assert
        mapped.IsSuccess.ShouldBeTrue();
        mapped.Value.ShouldBe(10);
    }

    [Fact]
    public void Map_WhenFailure_ShouldPreserveError()
    {
        // Arrange
        Error error = new("Test.Error", "Test message");
        Result<int> result = Result.Failure<int>(error);

        // Act
        Result<int> mapped = result.Map(v => v * 2);

        // Assert
        mapped.IsFailure.ShouldBeTrue();
        mapped.Error.ShouldBe(error);
    }

    [Fact]
    public void Bind_WhenSuccess_ShouldChainOperation()
    {
        // Arrange
        Result<int> result = Result.Success(5);

        // Act
        Result<string> bound = result.Bind(v => Result.Success(v.ToString()));

        // Assert
        bound.IsSuccess.ShouldBeTrue();
        bound.Value.ShouldBe("5");
    }

    [Fact]
    public void Bind_WhenSuccess_AndBinderReturnsFailure_ShouldReturnFailure()
    {
        // Arrange
        Result<int> result = Result.Success(5);
        Error error = new("Test.Error", "Test message");

        // Act
        Result<string> bound = result.Bind<int, string>(_ => Result.Failure<string>(error));

        // Assert
        bound.IsFailure.ShouldBeTrue();
        bound.Error.ShouldBe(error);
    }

    [Fact]
    public void Bind_WhenFailure_ShouldNotExecuteBinder()
    {
        // Arrange
        Error error = new("Test.Error", "Test message");
        Result<int> result = Result.Failure<int>(error);
        bool wasExecuted = false;

        // Act
        Result<string> bound = result.Bind(v =>
        {
            wasExecuted = true;
            return Result.Success(v.ToString());
        });

        // Assert
        wasExecuted.ShouldBeFalse();
        bound.IsFailure.ShouldBeTrue();
        bound.Error.ShouldBe(error);
    }

    [Fact]
    public void GetValueOrDefault_WhenSuccess_ShouldReturnValue()
    {
        // Arrange
        Result<string> result = Result.Success("test");

        // Act
        string value = result.GetValueOrDefault("default");

        // Assert
        value.ShouldBe("test");
    }

    [Fact]
    public void GetValueOrDefault_WhenFailure_ShouldReturnDefault()
    {
        // Arrange
        Error error = new("Test.Error", "Test message");
        Result<string> result = Result.Failure<string>(error);

        // Act
        string value = result.GetValueOrDefault("default");

        // Assert
        value.ShouldBe("default");
    }

    [Fact]
    public void Match_WhenSuccess_ShouldExecuteOnSuccess()
    {
        // Arrange
        Result<int> result = Result.Success(5);

        // Act
        string matched = result.Match(
            onSuccess: v => $"Success: {v}",
            onFailure: e => $"Failure: {e.Code}");

        // Assert
        matched.ShouldBe("Success: 5");
    }

    [Fact]
    public void Match_WhenFailure_ShouldExecuteOnFailure()
    {
        // Arrange
        Error error = new("Test.Error", "Test message");
        Result<int> result = Result.Failure<int>(error);

        // Act
        string matched = result.Match(
            onSuccess: v => $"Success: {v}",
            onFailure: e => $"Failure: {e.Code}");

        // Assert
        matched.ShouldBe("Failure: Test.Error");
    }

    [Fact]
    public void ToResult_WhenNotNull_ShouldReturnSuccess()
    {
        // Arrange
        string value = "test";
        Error error = new("Test.Error", "Test message");

        // Act
        Result<string> result = value.ToResult(error);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("test");
    }

    [Fact]
    public void ToResult_WhenNull_ShouldReturnFailure()
    {
        // Arrange
        string? value = null;
        Error error = new("Test.Error", "Test message");

        // Act
        Result<string> result = value.ToResult(error);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(error);
    }

    [Fact]
    public void Combine_AllSuccess_ShouldReturnCombinedValues()
    {
        // Arrange
        Result<int>[] results = new[]
        {
            Result.Success(1),
            Result.Success(2),
            Result.Success(3)
        };

        // Act
        Result<IReadOnlyList<int>> combined = results.Combine();

        // Assert
        combined.IsSuccess.ShouldBeTrue();
        combined.Value.ShouldBe(new[] { 1, 2, 3 });
    }

    [Fact]
    public void Combine_WithFailure_ShouldReturnFirstFailure()
    {
        // Arrange
        Error error = new("Test.Error", "Test message");
        Result<int>[] results = new[]
        {
            Result.Success(1),
            Result.Failure<int>(error),
            Result.Success(3)
        };

        // Act
        Result<IReadOnlyList<int>> combined = results.Combine();

        // Assert
        combined.IsFailure.ShouldBeTrue();
        combined.Error.ShouldBe(error);
    }

    [Fact]
    public void Combine_EmptyCollection_ShouldReturnEmptySuccess()
    {
        // Arrange
        Result<int>[] results = Array.Empty<Result<int>>();

        // Act
        Result<IReadOnlyList<int>> combined = results.Combine();

        // Assert
        combined.IsSuccess.ShouldBeTrue();
        combined.Value.ShouldBeEmpty();
    }
}
