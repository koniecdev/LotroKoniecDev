using LotroKoniecDev.Application.Progress;

namespace LotroKoniecDev.Tests.Unit.Tests.Progress;

public sealed class OperationProgressTests
{
    [Fact]
    public void Percentage_WithValidTotals_ShouldCalculateCorrectly()
    {
        // Arrange
        OperationProgress progress = new OperationProgress
        {
            OperationName = "Export",
            Current = 50,
            Total = 200
        };

        // Assert
        progress.Percentage.Should().Be(25.0);
    }

    [Fact]
    public void Percentage_WhenHalfDone_ShouldBeFifty()
    {
        // Arrange
        OperationProgress progress = new OperationProgress
        {
            OperationName = "Patch",
            Current = 500,
            Total = 1000
        };

        // Assert
        progress.Percentage.Should().Be(50.0);
    }

    [Fact]
    public void Percentage_WhenComplete_ShouldBeHundred()
    {
        // Arrange
        OperationProgress progress = new OperationProgress
        {
            OperationName = "Export",
            Current = 100,
            Total = 100
        };

        // Assert
        progress.Percentage.Should().Be(100.0);
    }

    [Fact]
    public void Percentage_WhenTotalIsZero_ShouldReturnZero()
    {
        // Arrange
        OperationProgress progress = new OperationProgress
        {
            OperationName = "Export",
            Current = 0,
            Total = 0
        };

        // Assert
        progress.Percentage.Should().Be(0.0);
    }

    [Fact]
    public void StatusMessage_WhenNotSet_ShouldBeNull()
    {
        // Arrange
        OperationProgress progress = new OperationProgress
        {
            OperationName = "Export",
            Current = 10,
            Total = 100
        };

        // Assert
        progress.StatusMessage.Should().BeNull();
    }

    [Fact]
    public void StatusMessage_WhenSet_ShouldReturnValue()
    {
        // Arrange
        OperationProgress progress = new OperationProgress
        {
            OperationName = "Patch",
            Current = 42,
            Total = 100,
            StatusMessage = "Processing file 620756992..."
        };

        // Assert
        progress.StatusMessage.Should().Be("Processing file 620756992...");
    }

    [Fact]
    public void OperationName_ShouldBePreserved()
    {
        // Arrange
        OperationProgress progress = new OperationProgress
        {
            OperationName = "Export",
            Current = 0,
            Total = 0
        };

        // Assert
        progress.OperationName.Should().Be("Export");
    }

    [Fact]
    public void Equality_SameValues_ShouldBeEqual()
    {
        // Arrange
        OperationProgress a = new OperationProgress
        {
            OperationName = "Export",
            Current = 10,
            Total = 100,
            StatusMessage = "test"
        };
        OperationProgress b = new OperationProgress
        {
            OperationName = "Export",
            Current = 10,
            Total = 100,
            StatusMessage = "test"
        };

        // Assert
        a.Should().Be(b);
    }

    [Fact]
    public void Equality_DifferentValues_ShouldNotBeEqual()
    {
        // Arrange
        OperationProgress a = new OperationProgress
        {
            OperationName = "Export",
            Current = 10,
            Total = 100
        };
        OperationProgress b = new OperationProgress
        {
            OperationName = "Patch",
            Current = 10,
            Total = 100
        };

        // Assert
        a.Should().NotBe(b);
    }
}
