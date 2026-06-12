using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.SharedKernel.Tests.Unit.Tests.Monads;

public sealed class ValueMaybeTests
{
    [Fact]
    public void From_WithValue_ShouldHaveValue()
    {
        // Act
        ValueMaybe<int> maybe = ValueMaybe<int>.From(42);

        // Assert
        maybe.HasValue.ShouldBeTrue();
        maybe.HasNoValue.ShouldBeFalse();
        maybe.Value.ShouldBe(42);
    }

    [Fact]
    public void From_WithNull_ShouldHaveNoValue()
    {
        // Act
        ValueMaybe<int> maybe = ValueMaybe<int>.From(null);

        // Assert
        maybe.HasNoValue.ShouldBeTrue();
    }

    [Fact]
    public void None_ShouldHaveNoValue()
    {
        // Act
        ValueMaybe<int> maybe = ValueMaybe<int>.None();

        // Assert
        maybe.HasNoValue.ShouldBeTrue();
    }

    [Fact]
    public void Value_WhenNone_ShouldThrowInvalidOperationException()
    {
        // Arrange
        ValueMaybe<int> maybe = ValueMaybe<int>.None();

        // Assert
        Should.Throw<InvalidOperationException>(() => _ = maybe.Value);
    }

    [Fact]
    public void Match_WhenValue_ShouldInvokeFromBranch()
    {
        // Arrange
        ValueMaybe<int> maybe = ValueMaybe<int>.From(42);

        // Act
        string matched = maybe.Match(value => $"value:{value}", () => "none");

        // Assert
        matched.ShouldBe("value:42");
    }

    [Fact]
    public void Match_WhenNone_ShouldInvokeNoneBranch()
    {
        // Arrange
        ValueMaybe<int> maybe = ValueMaybe<int>.None();

        // Act
        string matched = maybe.Match(value => $"value:{value}", () => "none");

        // Assert
        matched.ShouldBe("none");
    }

    [Fact]
    public void ImplicitOperator_FromValue_ShouldHaveValue()
    {
        // Act
        ValueMaybe<int> maybe = 42;

        // Assert
        maybe.HasValue.ShouldBeTrue();
        maybe.Value.ShouldBe(42);
    }
}
