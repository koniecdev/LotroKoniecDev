using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Tests.Integration.Models;

public class TranslationTests
{
    [Fact]
    public void GetPieces_SimpleSeparator_ShouldSplitCorrectly()
    {
        // Arrange
        Translation translation = new Translation
        {
            FileId = 1,
            GossipId = 100,
            Content = "Hello<--DO_NOT_TOUCH!-->World<--DO_NOT_TOUCH!-->!"
        };

        // Act
        string[] pieces = translation.GetPieces();

        // Assert
        pieces.Should().HaveCount(3);
        pieces[0].Should().Be("Hello");
        pieces[1].Should().Be("World");
        pieces[2].Should().Be("!");
    }

    [Fact]
    public void GetPieces_NoSeparator_ShouldReturnSinglePiece()
    {
        // Arrange
        Translation translation = new Translation
        {
            Content = "Simple text without separator"
        };

        // Act
        string[] pieces = translation.GetPieces();

        // Assert
        pieces.Should().HaveCount(1);
        pieces[0].Should().Be("Simple text without separator");
    }

    [Fact]
    public void GetUnescapedContent_WithEscapeSequences_ShouldUnescape()
    {
        // Arrange
        Translation translation = new Translation
        {
            Content = "Line1\\r\\nLine2\\r\\nLine3"
        };

        // Act
        string result = translation.GetUnescapedContent();

        // Assert
        result.Should().Be("Line1\r\nLine2\r\nLine3");
    }

    [Fact]
    public void HasArguments_WithArgsOrder_ShouldReturnTrue()
    {
        // Arrange
        Translation translation = new Translation
        {
            ArgsOrder = [0, 1, 2]
        };

        // Assert
        translation.HasArguments.Should().BeTrue();
    }

    [Fact]
    public void HasArguments_WithNullArgsOrder_ShouldReturnFalse()
    {
        // Arrange
        Translation translation = new Translation
        {
            ArgsOrder = null
        };

        // Assert
        translation.HasArguments.Should().BeFalse();
    }

    [Fact]
    public void HasArguments_WithEmptyArgsOrder_ShouldReturnFalse()
    {
        // Arrange
        Translation translation = new Translation
        {
            ArgsOrder = []
        };

        // Assert
        translation.HasArguments.Should().BeFalse();
    }

    [Fact]
    public void FragmentId_ShouldConvertFromGossipId()
    {
        // Arrange
        Translation translation = new Translation
        {
            GossipId = 12345
        };

        // Assert
        translation.FragmentId.Should().Be(12345UL);
    }

    [Fact]
    public void ToString_ShouldReturnDescriptiveString()
    {
        // Arrange
        Translation translation = new Translation
        {
            FileId = 100,
            GossipId = 200,
            Content = "Test content"
        };

        // Act
        string result = translation.ToString();

        // Assert
        result.Should().Contain("File=100");
        result.Should().Contain("Gossip=200");
        result.Should().Contain("Length=12");
    }
}
