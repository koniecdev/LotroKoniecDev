using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Primitives.Enums;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Core.Extensions;

namespace LotroKoniecDev.Tests.Integration.Extensions;

public class ResultExtensionsTests
{
    [Fact]
    public void OnSuccess_WhenSuccess_ShouldExecuteAction()
    {
        // Arrange
        var result = Result.Success(42);
        int capturedValue = 0;

        // Act
        result.OnSuccess(v => capturedValue = v);

        // Assert
        capturedValue.Should().Be(42);
    }

    [Fact]
    public void OnSuccess_WhenFailure_ShouldNotExecuteAction()
    {
        // Arrange
        var result = Result.Failure<int>(new Error("TEST", "Error"));
        bool actionCalled = false;

        // Act
        result.OnSuccess(_ => actionCalled = true);

        // Assert
        actionCalled.Should().BeFalse();
    }

    [Fact]
    public void OnFailure_WhenFailure_ShouldExecuteAction()
    {
        // Arrange
        var error = new Error("TEST", "Error message");
        var result = Result.Failure<int>(error);
        Error? capturedError = null;

        // Act
        result.OnFailure(e => capturedError = e);

        // Assert
        capturedError.Should().Be(error);
    }

    [Fact]
    public void OnFailure_WhenSuccess_ShouldNotExecuteAction()
    {
        // Arrange
        var result = Result.Success(42);
        bool actionCalled = false;

        // Act
        result.OnFailure(_ => actionCalled = true);

        // Assert
        actionCalled.Should().BeFalse();
    }

    [Fact]
    public void Map_WhenSuccess_ShouldTransformValue()
    {
        // Arrange
        var result = Result.Success(10);

        // Act
        var mapped = result.Map(x => x * 2);

        // Assert
        mapped.IsSuccess.Should().BeTrue();
        mapped.Value.Should().Be(20);
    }

    [Fact]
    public void Map_WhenFailure_ShouldPropagateError()
    {
        // Arrange
        var error = new Error("TEST", "Error");
        var result = Result.Failure<int>(error);

        // Act
        var mapped = result.Map(x => x * 2);

        // Assert
        mapped.IsFailure.Should().BeTrue();
        mapped.Error.Should().Be(error);
    }

    [Fact]
    public void Bind_WhenSuccess_ShouldChainOperations()
    {
        // Arrange
        var result = Result.Success(10);

        // Act
        var bound = result.Bind(x => Result.Success(x.ToString()));

        // Assert
        bound.IsSuccess.Should().BeTrue();
        bound.Value.Should().Be("10");
    }

    [Fact]
    public void Bind_WhenFirstFailure_ShouldPropagateError()
    {
        // Arrange
        var error = new Error("TEST", "First error");
        var result = Result.Failure<int>(error);

        // Act
        var bound = result.Bind(x => Result.Success(x.ToString()));

        // Assert
        bound.IsFailure.Should().BeTrue();
        bound.Error.Should().Be(error);
    }

    [Fact]
    public void Bind_WhenSecondFailure_ShouldReturnSecondError()
    {
        // Arrange
        var result = Result.Success(10);
        var error = new Error("TEST", "Second error");

        // Act
        var bound = result.Bind(_ => Result.Failure<string>(error));

        // Assert
        bound.IsFailure.Should().BeTrue();
        bound.Error.Should().Be(error);
    }

    [Fact]
    public void GetValueOrDefault_WhenSuccess_ShouldReturnValue()
    {
        // Arrange
        var result = Result.Success(42);

        // Act
        int value = result.GetValueOrDefault(0);

        // Assert
        value.Should().Be(42);
    }

    [Fact]
    public void GetValueOrDefault_WhenFailure_ShouldReturnDefault()
    {
        // Arrange
        var result = Result.Failure<int>(new Error("TEST", "Error"));

        // Act
        int value = result.GetValueOrDefault(99);

        // Assert
        value.Should().Be(99);
    }

    [Fact]
    public void Match_WhenSuccess_ShouldCallOnSuccess()
    {
        // Arrange
        var result = Result.Success(10);

        // Act
        string matched = result.Match(
            onSuccess: v => $"Value: {v}",
            onFailure: e => $"Error: {e.Message}");

        // Assert
        matched.Should().Be("Value: 10");
    }

    [Fact]
    public void Match_WhenFailure_ShouldCallOnFailure()
    {
        // Arrange
        var result = Result.Failure<int>(new Error("TEST", "Error message"));

        // Act
        string matched = result.Match(
            onSuccess: v => $"Value: {v}",
            onFailure: e => $"Error: {e.Message}");

        // Assert
        matched.Should().Be("Error: Error message");
    }

    [Fact]
    public void ToResult_WhenNotNull_ShouldReturnSuccess()
    {
        // Arrange
        string? value = "test";
        var error = new Error("TEST", "Value is null");

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
        var error = new Error("TEST", "Value is null");

        // Act
        var result = value.ToResult(error);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(error);
    }

    [Fact]
    public void Combine_AllSuccess_ShouldReturnAllValues()
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
    public void Combine_OneFailure_ShouldReturnFailure()
    {
        // Arrange
        var error = new Error("TEST", "Error in second");
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
    public void Combine_EmptyList_ShouldReturnEmptySuccess()
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
