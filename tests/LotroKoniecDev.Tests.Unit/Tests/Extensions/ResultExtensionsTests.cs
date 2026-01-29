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
        var result = Result.Success("test");
        var wasExecuted = false;
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
        var error = new Error("Test.Error", "Test message");
        var result = Result.Failure<string>(error);
        var wasExecuted = false;

        // Act
        result.OnSuccess(_ => wasExecuted = true);

        // Assert
        wasExecuted.Should().BeFalse();
    }

    [Fact]
    public void OnSuccess_ShouldReturnSameResult()
    {
        // Arrange
        var result = Result.Success("test");

        // Act
        var returned = result.OnSuccess(_ => { });

        // Assert
        returned.Should().BeSameAs(result);
    }

    [Fact]
    public void OnFailure_WhenFailure_ShouldExecuteAction()
    {
        // Arrange
        var error = new Error("Test.Error", "Test message");
        var result = Result.Failure<string>(error);
        var wasExecuted = false;
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
        var result = Result.Success("test");
        var wasExecuted = false;

        // Act
        result.OnFailure(_ => wasExecuted = true);

        // Assert
        wasExecuted.Should().BeFalse();
    }

    [Fact]
    public void Map_WhenSuccess_ShouldTransformValue()
    {
        // Arrange
        var result = Result.Success(5);

        // Act
        var mapped = result.Map(v => v * 2);

        // Assert
        mapped.IsSuccess.Should().BeTrue();
        mapped.Value.Should().Be(10);
    }

    [Fact]
    public void Map_WhenFailure_ShouldPreserveError()
    {
        // Arrange
        var error = new Error("Test.Error", "Test message");
        var result = Result.Failure<int>(error);

        // Act
        var mapped = result.Map(v => v * 2);

        // Assert
        mapped.IsFailure.Should().BeTrue();
        mapped.Error.Should().Be(error);
    }

    [Fact]
    public void Bind_WhenSuccess_ShouldChainOperation()
    {
        // Arrange
        var result = Result.Success(5);

        // Act
        var bound = result.Bind(v => Result.Success(v.ToString()));

        // Assert
        bound.IsSuccess.Should().BeTrue();
        bound.Value.Should().Be("5");
    }

    [Fact]
    public void Bind_WhenSuccess_AndBinderReturnsFailure_ShouldReturnFailure()
    {
        // Arrange
        var result = Result.Success(5);
        var error = new Error("Test.Error", "Test message");

        // Act
        var bound = result.Bind<int, string>(_ => Result.Failure<string>(error));

        // Assert
        bound.IsFailure.Should().BeTrue();
        bound.Error.Should().Be(error);
    }

    [Fact]
    public void Bind_WhenFailure_ShouldNotExecuteBinder()
    {
        // Arrange
        var error = new Error("Test.Error", "Test message");
        var result = Result.Failure<int>(error);
        var wasExecuted = false;

        // Act
        var bound = result.Bind(v =>
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
        var result = Result.Success("test");

        // Act
        var value = result.GetValueOrDefault("default");

        // Assert
        value.Should().Be("test");
    }

    [Fact]
    public void GetValueOrDefault_WhenFailure_ShouldReturnDefault()
    {
        // Arrange
        var error = new Error("Test.Error", "Test message");
        var result = Result.Failure<string>(error);

        // Act
        var value = result.GetValueOrDefault("default");

        // Assert
        value.Should().Be("default");
    }

    [Fact]
    public void Match_WhenSuccess_ShouldExecuteOnSuccess()
    {
        // Arrange
        var result = Result.Success(5);

        // Act
        var matched = result.Match(
            onSuccess: v => $"Success: {v}",
            onFailure: e => $"Failure: {e.Code}");

        // Assert
        matched.Should().Be("Success: 5");
    }

    [Fact]
    public void Match_WhenFailure_ShouldExecuteOnFailure()
    {
        // Arrange
        var error = new Error("Test.Error", "Test message");
        var result = Result.Failure<int>(error);

        // Act
        var matched = result.Match(
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
        var error = new Error("Test.Error", "Test message");

        // Act
        var result = value.ToResult(error);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("test");
    }

    [Fact]
    public void ToResult_WhenNull_ShouldReturnFailure()
    {
        // Arrange
        string? value = null;
        var error = new Error("Test.Error", "Test message");

        // Act
        var result = value.ToResult(error);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(error);
    }

    [Fact]
    public void Combine_AllSuccess_ShouldReturnCombinedValues()
    {
        // Arrange
        var results = new[]
        {
            Result.Success(1),
            Result.Success(2),
            Result.Success(3)
        };

        // Act
        var combined = results.Combine();

        // Assert
        combined.IsSuccess.Should().BeTrue();
        combined.Value.Should().BeEquivalentTo(new[] { 1, 2, 3 });
    }

    [Fact]
    public void Combine_WithFailure_ShouldReturnFirstFailure()
    {
        // Arrange
        var error = new Error("Test.Error", "Test message");
        var results = new[]
        {
            Result.Success(1),
            Result.Failure<int>(error),
            Result.Success(3)
        };

        // Act
        var combined = results.Combine();

        // Assert
        combined.IsFailure.Should().BeTrue();
        combined.Error.Should().Be(error);
    }

    [Fact]
    public void Combine_EmptyCollection_ShouldReturnEmptySuccess()
    {
        // Arrange
        var results = Array.Empty<Result<int>>();

        // Act
        var combined = results.Combine();

        // Assert
        combined.IsSuccess.Should().BeTrue();
        combined.Value.Should().BeEmpty();
    }
}
