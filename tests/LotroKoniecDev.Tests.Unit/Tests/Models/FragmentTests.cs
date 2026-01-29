using System.Text;
using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Tests.Unit.Tests.Models;

public sealed class FragmentTests
{
    [Fact]
    public void HasArguments_WithArgRefs_ShouldReturnTrue()
    {
        // Arrange
        var fragment = new Fragment();
        fragment.ArgRefs.Add([0x01, 0x00, 0x00, 0x00]);

        // Assert
        fragment.HasArguments.Should().BeTrue();
    }

    [Fact]
    public void HasArguments_WithoutArgRefs_ShouldReturnFalse()
    {
        // Arrange
        var fragment = new Fragment();

        // Assert
        fragment.HasArguments.Should().BeFalse();
    }

    [Fact]
    public void GetFullText_SinglePiece_ShouldReturnPieceText()
    {
        // Arrange
        var fragment = new Fragment();
        fragment.Pieces.Add("Hello World");

        // Act
        var result = fragment.GetFullText();

        // Assert
        result.Should().Be("Hello World");
    }

    [Fact]
    public void GetFullText_MultiplePieces_ShouldJoinWithEmptySeparator()
    {
        // Arrange
        var fragment = new Fragment();
        fragment.Pieces.AddRange(["Hello", " ", "World"]);

        // Act
        var result = fragment.GetFullText();

        // Assert
        result.Should().Be("Hello World");
    }

    [Fact]
    public void GetFullText_WithCustomSeparator_ShouldJoinWithSeparator()
    {
        // Arrange
        var fragment = new Fragment();
        fragment.Pieces.AddRange(["Line1", "Line2", "Line3"]);

        // Act
        var result = fragment.GetFullText("\n");

        // Assert
        result.Should().Be("Line1\nLine2\nLine3");
    }

    [Fact]
    public void Parse_NullReader_ShouldThrowArgumentNullException()
    {
        // Arrange
        var fragment = new Fragment();

        // Act
        var action = () => fragment.Parse(null!);

        // Assert
        action.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Parse_ValidData_ShouldParseFragmentId()
    {
        // Arrange
        var fragment = new Fragment();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write((ulong)123456789);
        writer.Write(0); // Num pieces
        writer.Write(0); // Num arg refs
        writer.Write((byte)0); // Num arg string groups

        stream.Position = 0;
        using var reader = new BinaryReader(stream);

        // Act
        fragment.Parse(reader);

        // Assert
        fragment.FragmentId.Should().Be(123456789UL);
    }

    [Fact]
    public void Parse_WithPieces_ShouldParsePiecesCorrectly()
    {
        // Arrange
        var fragment = new Fragment();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write((ulong)100);
        writer.Write(2); // Num pieces = 2
        writer.Write((byte)5); // Piece 1 length = 5 (VarLen)
        writer.Write(Encoding.Unicode.GetBytes("Hello"));
        writer.Write((byte)5); // Piece 2 length = 5 (VarLen)
        writer.Write(Encoding.Unicode.GetBytes("World"));
        writer.Write(0); // Num arg refs
        writer.Write((byte)0); // Num arg string groups

        stream.Position = 0;
        using var reader = new BinaryReader(stream);

        // Act
        fragment.Parse(reader);

        // Assert
        fragment.Pieces.Should().HaveCount(2);
        fragment.Pieces[0].Should().Be("Hello");
        fragment.Pieces[1].Should().Be("World");
    }

    [Fact]
    public void Parse_WithArgRefs_ShouldParseArgRefsCorrectly()
    {
        // Arrange
        var fragment = new Fragment();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write((ulong)100);
        writer.Write(0); // Num pieces
        writer.Write(2); // Num arg refs = 2
        writer.Write(new byte[] { 0x01, 0x02, 0x03, 0x04 }); // Arg ref 1
        writer.Write(new byte[] { 0x05, 0x06, 0x07, 0x08 }); // Arg ref 2
        writer.Write((byte)0); // Num arg string groups

        stream.Position = 0;
        using var reader = new BinaryReader(stream);

        // Act
        fragment.Parse(reader);

        // Assert
        fragment.ArgRefs.Should().HaveCount(2);
        fragment.ArgRefs[0].Should().BeEquivalentTo(new byte[] { 0x01, 0x02, 0x03, 0x04 });
        fragment.ArgRefs[1].Should().BeEquivalentTo(new byte[] { 0x05, 0x06, 0x07, 0x08 });
    }

    [Fact]
    public void Write_NullWriter_ShouldThrowArgumentNullException()
    {
        // Arrange
        var fragment = new Fragment();

        // Act
        var action = () => fragment.Write(null!);

        // Assert
        action.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ParseAndWrite_RoundTrip_ShouldPreserveData()
    {
        // Arrange
        using var originalStream = new MemoryStream();
        using var originalWriter = new BinaryWriter(originalStream);

        originalWriter.Write((ulong)98765);
        originalWriter.Write(2); // Num pieces
        originalWriter.Write((byte)4); // Piece 1 length
        originalWriter.Write(Encoding.Unicode.GetBytes("Test"));
        originalWriter.Write((byte)4); // Piece 2 length
        originalWriter.Write(Encoding.Unicode.GetBytes("Data"));
        originalWriter.Write(1); // Num arg refs
        originalWriter.Write(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });
        originalWriter.Write((byte)0); // Num arg string groups

        var originalData = originalStream.ToArray();

        // Parse
        var fragment = new Fragment();
        using var parseStream = new MemoryStream(originalData);
        using var parseReader = new BinaryReader(parseStream);
        fragment.Parse(parseReader);

        // Write
        using var writeStream = new MemoryStream();
        using var writeWriter = new BinaryWriter(writeStream);
        fragment.Write(writeWriter);

        var writtenData = writeStream.ToArray();

        // Assert - Re-parse and compare
        var reparsedFragment = new Fragment();
        using var reparseStream = new MemoryStream(writtenData);
        using var reparseReader = new BinaryReader(reparseStream);
        reparsedFragment.Parse(reparseReader);

        reparsedFragment.FragmentId.Should().Be(98765UL);
        reparsedFragment.Pieces.Should().BeEquivalentTo(["Test", "Data"]);
        reparsedFragment.ArgRefs.Should().HaveCount(1);
    }
}
