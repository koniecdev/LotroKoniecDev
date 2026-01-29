using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Tests.Unit.Tests.Models;

public sealed class TranslationTests
{
    [Fact]
    public void HasArguments_WithArgsOrder_ShouldReturnTrue()
    {
        // Arrange
        var translation = new Translation
        {
            FileId = 1,
            GossipId = 100,
            Content = "Test content",
            ArgsOrder = [0, 1, 2],
            ArgsId = [1, 2, 3]
        };

        // Assert
        translation.HasArguments.Should().BeTrue();
    }

    [Fact]
    public void HasArguments_WithNullArgsOrder_ShouldReturnFalse()
    {
        // Arrange
        var translation = new Translation
        {
            FileId = 1,
            GossipId = 100,
            Content = "Test content",
            ArgsOrder = null,
            ArgsId = null
        };

        // Assert
        translation.HasArguments.Should().BeFalse();
    }

    [Fact]
    public void HasArguments_WithEmptyArgsOrder_ShouldReturnFalse()
    {
        // Arrange
        var translation = new Translation
        {
            FileId = 1,
            GossipId = 100,
            Content = "Test content",
            ArgsOrder = [],
            ArgsId = []
        };

        // Assert
        translation.HasArguments.Should().BeFalse();
    }

    [Fact]
    public void FragmentId_ShouldReturnGossipIdAsUlong()
    {
        // Arrange
        var translation = new Translation
        {
            FileId = 1,
            GossipId = 12345,
            Content = "Test"
        };

        // Assert
        translation.FragmentId.Should().Be(12345UL);
    }

    [Fact]
    public void GetPieces_WithoutSeparator_ShouldReturnSinglePiece()
    {
        // Arrange
        var translation = new Translation
        {
            FileId = 1,
            GossipId = 100,
            Content = "Simple text content"
        };

        // Act
        var pieces = translation.GetPieces();

        // Assert
        pieces.Should().HaveCount(1);
        pieces[0].Should().Be("Simple text content");
    }

    [Fact]
    public void GetPieces_WithSeparator_ShouldSplitContent()
    {
        // Arrange
        var translation = new Translation
        {
            FileId = 1,
            GossipId = 100,
            Content = "Part1<--DO_NOT_TOUCH!-->Part2<--DO_NOT_TOUCH!-->Part3"
        };

        // Act
        var pieces = translation.GetPieces();

        // Assert
        pieces.Should().HaveCount(3);
        pieces[0].Should().Be("Part1");
        pieces[1].Should().Be("Part2");
        pieces[2].Should().Be("Part3");
    }

    [Fact]
    public void GetPieces_WithEmptyParts_ShouldPreserveEmptyStrings()
    {
        // Arrange
        var translation = new Translation
        {
            FileId = 1,
            GossipId = 100,
            Content = "<--DO_NOT_TOUCH!-->Middle<--DO_NOT_TOUCH!-->"
        };

        // Act
        var pieces = translation.GetPieces();

        // Assert
        pieces.Should().HaveCount(3);
        pieces[0].Should().BeEmpty();
        pieces[1].Should().Be("Middle");
        pieces[2].Should().BeEmpty();
    }

    [Fact]
    public void GetUnescapedContent_ShouldUnescapeNewlines()
    {
        // Arrange
        var translation = new Translation
        {
            FileId = 1,
            GossipId = 100,
            Content = "Line1\\nLine2\\rLine3"
        };

        // Act
        var unescaped = translation.GetUnescapedContent();

        // Assert
        unescaped.Should().Be("Line1\nLine2\rLine3");
    }

    [Fact]
    public void GetUnescapedContent_WithoutEscapes_ShouldReturnSameContent()
    {
        // Arrange
        var translation = new Translation
        {
            FileId = 1,
            GossipId = 100,
            Content = "Normal content without escapes"
        };

        // Act
        var unescaped = translation.GetUnescapedContent();

        // Assert
        unescaped.Should().Be("Normal content without escapes");
    }

    [Fact]
    public void ToString_ShouldReturnFormattedString()
    {
        // Arrange
        var translation = new Translation
        {
            FileId = 12345,
            GossipId = 67890,
            Content = "Test content here"
        };

        // Act
        var result = translation.ToString();

        // Assert
        result.Should().Be("Translation[File=12345, Gossip=67890, Length=17]");
    }
}
