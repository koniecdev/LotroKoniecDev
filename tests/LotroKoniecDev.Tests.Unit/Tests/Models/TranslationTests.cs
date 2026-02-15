using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Tests.Unit.Tests.Models;

public sealed class TranslationTests
{
    [Fact]
    public void HasArguments_WithArgsOrder_ShouldReturnTrue()
    {
        // Arrange
        Translation translation = new()
        {
            FileId = 1,
            GossipId = 100,
            Content = "Test content",
            ArgsOrder = [0, 1, 2],
            ArgsId = [1, 2, 3]
        };

        // Assert
        translation.HasArguments.ShouldBeTrue();
    }

    [Fact]
    public void HasArguments_WithNullArgsOrder_ShouldReturnFalse()
    {
        // Arrange
        Translation translation = new()
        {
            FileId = 1,
            GossipId = 100,
            Content = "Test content",
            ArgsOrder = null,
            ArgsId = null
        };

        // Assert
        translation.HasArguments.ShouldBeFalse();
    }

    [Fact]
    public void HasArguments_WithEmptyArgsOrder_ShouldReturnFalse()
    {
        // Arrange
        Translation translation = new()
        {
            FileId = 1,
            GossipId = 100,
            Content = "Test content",
            ArgsOrder = [],
            ArgsId = []
        };

        // Assert
        translation.HasArguments.ShouldBeFalse();
    }

    [Fact]
    public void FragmentId_ShouldReturnGossipIdAsUlong()
    {
        // Arrange
        Translation translation = new()
        {
            FileId = 1,
            GossipId = 12345,
            Content = "Test"
        };

        // Assert
        translation.FragmentId.ShouldBe(12345UL);
    }

    [Fact]
    public void GetPieces_WithoutSeparator_ShouldReturnSinglePiece()
    {
        // Arrange
        Translation translation = new()
        {
            FileId = 1,
            GossipId = 100,
            Content = "Simple text content"
        };

        // Act
        string[] pieces = translation.GetPieces();

        // Assert
        pieces.Length.ShouldBe(1);
        pieces[0].ShouldBe("Simple text content");
    }

    [Fact]
    public void GetPieces_WithSeparator_ShouldSplitContent()
    {
        // Arrange
        Translation translation = new()
        {
            FileId = 1,
            GossipId = 100,
            Content = "Part1<--DO_NOT_TOUCH!-->Part2<--DO_NOT_TOUCH!-->Part3"
        };

        // Act
        string[] pieces = translation.GetPieces();

        // Assert
        pieces.Length.ShouldBe(3);
        pieces[0].ShouldBe("Part1");
        pieces[1].ShouldBe("Part2");
        pieces[2].ShouldBe("Part3");
    }

    [Fact]
    public void GetPieces_WithEmptyParts_ShouldPreserveEmptyStrings()
    {
        // Arrange
        Translation translation = new()
        {
            FileId = 1,
            GossipId = 100,
            Content = "<--DO_NOT_TOUCH!-->Middle<--DO_NOT_TOUCH!-->"
        };

        // Act
        string[] pieces = translation.GetPieces();

        // Assert
        pieces.Length.ShouldBe(3);
        pieces[0].ShouldBeEmpty();
        pieces[1].ShouldBe("Middle");
        pieces[2].ShouldBeEmpty();
    }

    [Fact]
    public void GetUnescapedContent_ShouldUnescapeNewlines()
    {
        // Arrange
        Translation translation = new()
        {
            FileId = 1,
            GossipId = 100,
            Content = "Line1\\nLine2\\rLine3"
        };

        // Act
        string unescaped = translation.GetUnescapedContent();

        // Assert
        unescaped.ShouldBe("Line1\nLine2\rLine3");
    }

    [Fact]
    public void GetUnescapedContent_WithoutEscapes_ShouldReturnSameContent()
    {
        // Arrange
        Translation translation = new()
        {
            FileId = 1,
            GossipId = 100,
            Content = "Normal content without escapes"
        };

        // Act
        string unescaped = translation.GetUnescapedContent();

        // Assert
        unescaped.ShouldBe("Normal content without escapes");
    }

    [Fact]
    public void ToString_ShouldReturnFormattedString()
    {
        // Arrange
        Translation translation = new()
        {
            FileId = 12345,
            GossipId = 67890,
            Content = "Test content here"
        };

        // Act
        string result = translation.ToString();

        // Assert
        result.ShouldBe("Translation[File=12345, Gossip=67890, Length=17]");
    }
}
