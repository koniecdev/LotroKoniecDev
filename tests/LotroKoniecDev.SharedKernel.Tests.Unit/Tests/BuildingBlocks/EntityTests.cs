using LotroKoniecDev.SharedKernel.BuildingBlocks;

namespace LotroKoniecDev.SharedKernel.Tests.Unit.Tests.BuildingBlocks;

public sealed class EntityTests
{
    private sealed class FirstEntity : Entity<Guid>
    {
        public FirstEntity(Guid id) : base(id)
        {
        }
    }

    private sealed class SecondEntity : Entity<Guid>
    {
        public SecondEntity(Guid id) : base(id)
        {
        }
    }

    [Fact]
    public void Equals_SameId_ShouldBeTrue()
    {
        // Arrange
        Guid id = Guid.NewGuid();
        FirstEntity first = new(id);
        FirstEntity second = new(id);

        // Assert
        first.Equals(second).ShouldBeTrue();
        (first == second).ShouldBeTrue();
    }

    [Fact]
    public void Equals_DifferentId_ShouldBeFalse()
    {
        // Arrange
        FirstEntity first = new(Guid.NewGuid());
        FirstEntity second = new(Guid.NewGuid());

        // Assert
        first.Equals(second).ShouldBeFalse();
        (first != second).ShouldBeTrue();
    }

    [Fact]
    public void Equals_DifferentTypeSameId_ShouldBeFalse()
    {
        // Arrange
        Guid id = Guid.NewGuid();
        FirstEntity first = new(id);
        SecondEntity second = new(id);

        // Assert
        first.Equals((object)second).ShouldBeFalse();
    }

    [Fact]
    public void OperatorEquals_BothNull_ShouldBeTrue()
    {
        // Arrange
        FirstEntity? first = null;
        FirstEntity? second = null;

        // Assert
        (first == second).ShouldBeTrue();
    }

    [Fact]
    public void OperatorEquals_OneNull_ShouldBeFalse()
    {
        // Arrange
        FirstEntity first = new(Guid.NewGuid());
        FirstEntity? second = null;

        // Assert
        (first == second).ShouldBeFalse();
    }

    [Fact]
    public void GetHashCode_SameId_ShouldBeEqual()
    {
        // Arrange
        Guid id = Guid.NewGuid();
        FirstEntity first = new(id);
        FirstEntity second = new(id);

        // Assert
        first.GetHashCode().ShouldBe(second.GetHashCode());
    }
}
