using LotroKoniecDev.Domain.Core.BuildingBlocks;

namespace LotroKoniecDev.Tests.Unit.Tests.Core.BuildingBlocks;

public sealed class ValueObjectTests
{
    private sealed class TestValueObject : ValueObject
    {
        public string Value1 { get; }
        public int Value2 { get; }

        public TestValueObject(string value1, int value2)
        {
            Value1 = value1;
            Value2 = value2;
        }

        protected override IEnumerable<object> GetAtomicValues()
        {
            yield return Value1;
            yield return Value2;
        }
    }

    [Fact]
    public void Equals_SameValues_ShouldReturnTrue()
    {
        // Arrange
        var obj1 = new TestValueObject("test", 42);
        var obj2 = new TestValueObject("test", 42);

        // Act & Assert
        obj1.Equals(obj2).Should().BeTrue();
    }

    [Fact]
    public void Equals_DifferentValues_ShouldReturnFalse()
    {
        // Arrange
        var obj1 = new TestValueObject("test", 42);
        var obj2 = new TestValueObject("test", 43);

        // Act & Assert
        obj1.Equals(obj2).Should().BeFalse();
    }

    [Fact]
    public void Equals_WithNull_ShouldReturnFalse()
    {
        // Arrange
        var obj = new TestValueObject("test", 42);

        // Act & Assert
        obj.Equals(null).Should().BeFalse();
    }

    [Fact]
    public void EqualsObject_SameValues_ShouldReturnTrue()
    {
        // Arrange
        var obj1 = new TestValueObject("test", 42);
        object obj2 = new TestValueObject("test", 42);

        // Act & Assert
        obj1.Equals(obj2).Should().BeTrue();
    }

    [Fact]
    public void EqualsObject_DifferentType_ShouldReturnFalse()
    {
        // Arrange
        var obj1 = new TestValueObject("test", 42);
        object obj2 = "not a value object";

        // Act & Assert
        obj1.Equals(obj2).Should().BeFalse();
    }

    [Fact]
    public void EqualityOperator_SameValues_ShouldReturnTrue()
    {
        // Arrange
        var obj1 = new TestValueObject("test", 42);
        var obj2 = new TestValueObject("test", 42);

        // Act & Assert
        (obj1 == obj2).Should().BeTrue();
    }

    [Fact]
    public void EqualityOperator_DifferentValues_ShouldReturnFalse()
    {
        // Arrange
        var obj1 = new TestValueObject("test", 42);
        var obj2 = new TestValueObject("test", 43);

        // Act & Assert
        (obj1 == obj2).Should().BeFalse();
    }

    [Fact]
    public void InequalityOperator_DifferentValues_ShouldReturnTrue()
    {
        // Arrange
        var obj1 = new TestValueObject("test", 42);
        var obj2 = new TestValueObject("test", 43);

        // Act & Assert
        (obj1 != obj2).Should().BeTrue();
    }

    [Fact]
    public void EqualityOperator_BothNull_ShouldReturnTrue()
    {
        // Arrange
        TestValueObject? obj1 = null;
        TestValueObject? obj2 = null;

        // Act & Assert
        (obj1 == obj2).Should().BeTrue();
    }

    [Fact]
    public void EqualityOperator_OneNull_ShouldReturnFalse()
    {
        // Arrange
        var obj1 = new TestValueObject("test", 42);
        TestValueObject? obj2 = null;

        // Act & Assert
        (obj1 == obj2).Should().BeFalse();
        (obj2 == obj1).Should().BeFalse();
    }

    [Fact]
    public void GetHashCode_SameValues_ShouldReturnSameHashCode()
    {
        // Arrange
        var obj1 = new TestValueObject("test", 42);
        var obj2 = new TestValueObject("test", 42);

        // Act & Assert
        obj1.GetHashCode().Should().Be(obj2.GetHashCode());
    }

    [Fact]
    public void GetHashCode_DifferentValues_ShouldReturnDifferentHashCode()
    {
        // Arrange
        var obj1 = new TestValueObject("test", 42);
        var obj2 = new TestValueObject("test", 43);

        // Act & Assert
        obj1.GetHashCode().Should().NotBe(obj2.GetHashCode());
    }
}
