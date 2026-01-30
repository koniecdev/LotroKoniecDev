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
        wasExecuted.Should().BeTrue();
        capturedValue.Should().Be("test");
    }

    [Fact]
    public void OnSuccess_WhenFailure_ShouldNotExecuteAction()
    {
        // Arrange
        Error error = new Error("Test.Error", "Test message");
        Result<string> result = Result.Failure<string>(error);
        bool wasExecuted = false;

        // Act
        result.OnSuccess(_ => wasExecuted = true);

        // Assert
        wasExecuted.Should().BeFalse();
    }

    [Fact]
    public void OnSuccess_ShouldReturnSameResult()
    {
        // Arrange
        Result<string> result = Result.Success("test");

        // Act
        Result<string> returned = result.OnSuccess(_ => { });

        // Assert
        returned.Should().BeSameAs(result);
    }

    [Fact]
    public void OnFailure_WhenFailure_ShouldExecuteAction()
    {
        // Arrange
        Error error = new Error("Test.Error", "Test message");
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
        wasExecuted.Should().BeTrue();
        capturedError.Should().Be(error);
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
        wasExecuted.Should().BeFalse();
    }

    [Fact]
    public void Map_WhenSuccess_ShouldTransformValue()
    {
        // Arrange
        Result<int> result = Result.Success(5);

        // Act
        Result<int> mapped = result.Map(v => v * 2);

        // Assert
        mapped.IsSuccess.Should().BeTrue();
        mapped.Value.Should().Be(10);
    }

    [Fact]
    public void Map_WhenFailure_ShouldPreserveError()
    {
        // Arrange
        Error error = new Error("Test.Error", "Test message");
        Result<int> result = Result.Failure<int>(error);

        // Act
        Result<int> mapped = result.Map(v => v * 2);

        // Assert
        mapped.IsFailure.Should().BeTrue();
        mapped.Error.Should().Be(error);
    }

    [Fact]
    public void Bind_WhenSuccess_ShouldChainOperation()
    {
        // Arrange
        Result<int> result = Result.Success(5);

        // Act
        Result<string> bound = result.Bind(v => Result.Success(v.ToString()));

        // Assert
        bound.IsSuccess.Should().BeTrue();
        bound.Value.Should().Be("5");
    }

    [Fact]
    public void Bind_WhenSuccess_AndBinderReturnsFailure_ShouldReturnFailure()
    {
        // Arrange
        Result<int> result = Result.Success(5);
        Error error = new Error("Test.Error", "Test message");

        // Act
        Result<string> bound = result.Bind<int, string>(_ => Result.Failure<string>(error));

        // Assert
        bound.IsFailure.Should().BeTrue();
        bound.Error.Should().Be(error);
    }

    [Fact]
    public void Bind_WhenFailure_ShouldNotExecuteBinder()
    {
        // Arrange
        Error error = new Error("Test.Error", "Test message");
        Result<int> result = Result.Failure<int>(error);
        bool wasExecuted = false;

        // Act
        Result<string> bound = result.Bind(v =>
        {
            wasExecuted = true;
            return Result.Success(v.ToString());
        });

        // Assert
        wasExecuted.Should().BeFalse();
        bound.IsFailure.Should().BeTrue();
        bound.Error.Should().Be(error);
    }

    [Fact]
    public void GetValueOrDefault_WhenSuccess_ShouldReturnValue()
    {
        // Arrange
        Result<string> result = Result.Success("test");

        // Act
        string value = result.GetValueOrDefault("default");

        // Assert
        value.Should().Be("test");
    }

    [Fact]
    public void GetValueOrDefault_WhenFailure_ShouldReturnDefault()
    {
        // Arrange
        Error error = new Error("Test.Error", "Test message");
        Result<string> result = Result.Failure<string>(error);

        // Act
        string value = result.GetValueOrDefault("default");

        // Assert
        value.Should().Be("default");
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
        matched.Should().Be("Success: 5");
    }

    [Fact]
    public void Match_WhenFailure_ShouldExecuteOnFailure()
    {
        // Arrange
        Error error = new Error("Test.Error", "Test message");
        Result<int> result = Result.Failure<int>(error);

        // Act
        string matched = result.Match(
            onSuccess: v => $"Success: {v}",
            onFailure: e => $"Failure: {e.Code}");

        // Assert
        matched.Should().Be("Failure: Test.Error");
    }

    [Fact]
    public void ToResult_WhenNotNull_ShouldReturnSuccess()
    {
        // Arrange
        string value = "test";
        Error error = new Error("Test.Error", "Test message");

        // Act
        Result<string> result = value.ToResult(error);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("test");
    }

    [Fact]
    public void ToResult_WhenNull_ShouldReturnFailure()
    {
        // Arrange
        string? value = null;
        Error error = new Error("Test.Error", "Test message");

        // Act
        Result<string> result = value.ToResult(error);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(error);
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
        combined.IsSuccess.Should().BeTrue();
        combined.Value.Should().BeEquivalentTo(new[] { 1, 2, 3 });
    }

    [Fact]
    public void Combine_WithFailure_ShouldReturnFirstFailure()
    {
        // Arrange
        Error error = new Error("Test.Error", "Test message");
        Result<int>[] results = new[]
        {
            Result.Success(1),
            Result.Failure<int>(error),
            Result.Success(3)
        };

        // Act
        Result<IReadOnlyList<int>> combined = results.Combine();

        // Assert
        combined.IsFailure.Should().BeTrue();
        combined.Error.Should().Be(error);
    }

    [Fact]
    public void Combine_EmptyCollection_ShouldReturnEmptySuccess()
    {
        // Arrange
        Result<int>[] results = Array.Empty<Result<int>>();

        // Act
        Result<IReadOnlyList<int>> combined = results.Combine();

        // Assert
        combined.IsSuccess.Should().BeTrue();
        combined.Value.Should().BeEmpty();
    }
}
