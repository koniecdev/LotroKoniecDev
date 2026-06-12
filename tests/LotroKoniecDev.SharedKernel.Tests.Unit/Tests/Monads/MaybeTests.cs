using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.SharedKernel.Tests.Unit.Tests.Monads;

public sealed class MaybeTests
{
    [Fact]
    public void From_WithValue_ShouldHaveValue()
    {
        // Act
        Maybe<string> maybe = Maybe<string>.From("value");

        // Assert
        maybe.HasValue.ShouldBeTrue();
        maybe.HasNoValue.ShouldBeFalse();
        maybe.Value.ShouldBe("value");
    }

    [Fact]
    public void From_WithNull_ShouldHaveNoValue()
    {
        // Act
        Maybe<string> maybe = Maybe<string>.From(null);

        // Assert
        maybe.HasValue.ShouldBeFalse();
        maybe.HasNoValue.ShouldBeTrue();
    }

    [Fact]
    public void None_ShouldHaveNoValue()
    {
        // Act
        Maybe<string> maybe = Maybe<string>.None;

        // Assert
        maybe.HasNoValue.ShouldBeTrue();
    }

    [Fact]
    public void Value_WhenNone_ShouldThrowInvalidOperationException()
    {
        // Arrange
        Maybe<string> maybe = Maybe<string>.None;

        // Assert
        Should.Throw<InvalidOperationException>(() => _ = maybe.Value);
    }

    [Fact]
    public void ImplicitOperator_FromValue_ShouldHaveValue()
    {
        // Act
        Maybe<string> maybe = "value";

        // Assert
        maybe.HasValue.ShouldBeTrue();
        maybe.Value.ShouldBe("value");
    }

    [Fact]
    public void ImplicitOperator_ToNullable_WhenNone_ShouldBeNull()
    {
        // Arrange
        Maybe<string> maybe = Maybe<string>.None;

        // Act
        string? value = maybe;

        // Assert
        value.ShouldBeNull();
    }

    [Fact]
    public void ImplicitOperator_ToNullable_WhenValue_ShouldReturnValue()
    {
        // Arrange
        Maybe<string> maybe = Maybe<string>.From("value");

        // Act
        string? value = maybe;

        // Assert
        value.ShouldBe("value");
    }

    [Fact]
    public void Equals_BothNone_ShouldBeTrue()
    {
        // Arrange
        Maybe<string> first = Maybe<string>.None;
        Maybe<string> second = Maybe<string>.None;

        // Assert
        first.Equals(second).ShouldBeTrue();
    }

    [Fact]
    public void Equals_NoneAndValue_ShouldBeFalse()
    {
        // Arrange
        Maybe<string> none = Maybe<string>.None;
        Maybe<string> some = Maybe<string>.From("value");

        // Assert
        none.Equals(some).ShouldBeFalse();
        some.Equals(none).ShouldBeFalse();
    }

    [Fact]
    public void Equals_SameValues_ShouldBeTrue()
    {
        // Arrange
        Maybe<string> first = Maybe<string>.From("value");
        Maybe<string> second = Maybe<string>.From("value");

        // Assert
        first.Equals(second).ShouldBeTrue();
    }

    [Fact]
    public void Equals_DifferentValues_ShouldBeFalse()
    {
        // Arrange
        Maybe<string> first = Maybe<string>.From("first");
        Maybe<string> second = Maybe<string>.From("second");

        // Assert
        first.Equals(second).ShouldBeFalse();
    }

    [Fact]
    public void Equals_RawValue_ShouldCompareAgainstWrappedValue()
    {
        // Arrange
        Maybe<string> maybe = Maybe<string>.From("value");

        // Assert
        maybe.Equals((object)"value").ShouldBeTrue();
    }

    [Fact]
    public void GetHashCode_WhenNone_ShouldBeZero()
    {
        // Arrange
        Maybe<string> maybe = Maybe<string>.None;

        // Assert
        maybe.GetHashCode().ShouldBe(0);
    }

    [Fact]
    public void GetHashCode_WhenValue_ShouldBeValueHashCode()
    {
        // Arrange
        Maybe<string> maybe = Maybe<string>.From("value");

        // Assert
        maybe.GetHashCode().ShouldBe("value".GetHashCode());
    }

    [Fact]
    public void Equals_UnrelatedType_ShouldBeFalse()
    {
        // Arrange
        Maybe<string> maybe = Maybe<string>.From("value");

        // Assert
        maybe.Equals(42).ShouldBeFalse();
    }
}
