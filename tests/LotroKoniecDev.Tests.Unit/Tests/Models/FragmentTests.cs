using System.Text;
using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Tests.Unit.Tests.Models;

public sealed class FragmentTests
{
    [Fact]
    public void HasArguments_WithArgRefs_ShouldReturnTrue()
    {
        // Arrange
        Fragment fragment = new();
        fragment.ArgRefs.Add([0x01, 0x00, 0x00, 0x00]);

        // Assert
        fragment.HasArguments.ShouldBeTrue();
    }

    [Fact]
    public void HasArguments_WithoutArgRefs_ShouldReturnFalse()
    {
        // Arrange
        Fragment fragment = new();

        // Assert
        fragment.HasArguments.ShouldBeFalse();
    }

    [Fact]
    public void GetFullText_SinglePiece_ShouldReturnPieceText()
    {
        // Arrange
        Fragment fragment = new();
        fragment.Pieces.Add("Hello World");

        // Act
        string result = fragment.GetFullText();

        // Assert
        result.ShouldBe("Hello World");
    }

    [Fact]
    public void GetFullText_MultiplePieces_ShouldJoinWithEmptySeparator()
    {
        // Arrange
        Fragment fragment = new();
        fragment.Pieces.AddRange(["Hello", " ", "World"]);

        // Act
        string result = fragment.GetFullText();

        // Assert
        result.ShouldBe("Hello World");
    }

    [Fact]
    public void GetFullText_WithCustomSeparator_ShouldJoinWithSeparator()
    {
        // Arrange
        Fragment fragment = new();
        fragment.Pieces.AddRange(["Line1", "Line2", "Line3"]);

        // Act
        string result = fragment.GetFullText("\n");

        // Assert
        result.ShouldBe("Line1\nLine2\nLine3");
    }

    [Fact]
    public void Parse_NullReader_ShouldThrowArgumentNullException()
    {
        // Arrange
        Fragment fragment = new();

        // Act
        Action action = () => fragment.Parse(null!);

        // Assert
        action.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public void Parse_ValidData_ShouldParseFragmentId()
    {
        // Arrange
        Fragment fragment = new();
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        writer.Write((ulong)123456789);
        writer.Write(0); // Num pieces
        writer.Write(0); // Num arg refs
        writer.Write((byte)0); // Num arg string groups

        stream.Position = 0;
        using BinaryReader reader = new(stream);

        // Act
        fragment.Parse(reader);

        // Assert
        fragment.FragmentId.ShouldBe(123456789UL);
    }

    [Fact]
    public void Parse_WithPieces_ShouldParsePiecesCorrectly()
    {
        // Arrange
        Fragment fragment = new();
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        writer.Write((ulong)100);
        writer.Write(2); // Num pieces = 2
        writer.Write((byte)5); // Piece 1 length = 5 (VarLen)
        writer.Write(Encoding.Unicode.GetBytes("Hello"));
        writer.Write((byte)5); // Piece 2 length = 5 (VarLen)
        writer.Write(Encoding.Unicode.GetBytes("World"));
        writer.Write(0); // Num arg refs
        writer.Write((byte)0); // Num arg string groups

        stream.Position = 0;
        using BinaryReader reader = new(stream);

        // Act
        fragment.Parse(reader);

        // Assert
        fragment.Pieces.Count.ShouldBe(2);
        fragment.Pieces[0].ShouldBe("Hello");
        fragment.Pieces[1].ShouldBe("World");
    }

    [Fact]
    public void Parse_WithArgRefs_ShouldParseArgRefsCorrectly()
    {
        // Arrange
        Fragment fragment = new();
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        writer.Write((ulong)100);
        writer.Write(0); // Num pieces
        writer.Write(2); // Num arg refs = 2
        writer.Write(new byte[] { 0x01, 0x02, 0x03, 0x04 }); // Arg ref 1
        writer.Write(new byte[] { 0x05, 0x06, 0x07, 0x08 }); // Arg ref 2
        writer.Write((byte)0); // Num arg string groups

        stream.Position = 0;
        using BinaryReader reader = new(stream);

        // Act
        fragment.Parse(reader);

        // Assert
        fragment.ArgRefs.Count.ShouldBe(2);
        fragment.ArgRefs[0].ShouldBeEquivalentTo(new byte[] { 0x01, 0x02, 0x03, 0x04 });
        fragment.ArgRefs[1].ShouldBeEquivalentTo(new byte[] { 0x05, 0x06, 0x07, 0x08 });
    }

    [Fact]
    public void Write_NullWriter_ShouldThrowArgumentNullException()
    {
        // Arrange
        Fragment fragment = new();

        // Act
        Action action = () => fragment.Write(null!);

        // Assert
        action.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public void ParseAndWrite_RoundTrip_ShouldPreserveData()
    {
        // Arrange
        using MemoryStream originalStream = new();
        using BinaryWriter originalWriter = new(originalStream);

        originalWriter.Write((ulong)98765);
        originalWriter.Write(2); // Num pieces
        originalWriter.Write((byte)4); // Piece 1 length
        originalWriter.Write(Encoding.Unicode.GetBytes("Test"));
        originalWriter.Write((byte)4); // Piece 2 length
        originalWriter.Write(Encoding.Unicode.GetBytes("Data"));
        originalWriter.Write(1); // Num arg refs
        originalWriter.Write(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });
        originalWriter.Write((byte)0); // Num arg string groups

        byte[] originalData = originalStream.ToArray();

        // Parse
        Fragment fragment = new();
        using MemoryStream parseStream = new(originalData);
        using BinaryReader parseReader = new(parseStream);
        fragment.Parse(parseReader);

        // Write
        using MemoryStream writeStream = new();
        using BinaryWriter writeWriter = new(writeStream);
        fragment.Write(writeWriter);

        byte[] writtenData = writeStream.ToArray();

        // Assert - Re-parse and compare
        Fragment reparsedFragment = new();
        using MemoryStream reparseStream = new(writtenData);
        using BinaryReader reparseReader = new(reparseStream);
        reparsedFragment.Parse(reparseReader);

        reparsedFragment.FragmentId.ShouldBe(98765UL);
        reparsedFragment.Pieces.ShouldBe(new[] { "Test", "Data" });
        reparsedFragment.ArgRefs.Count.ShouldBe(1);
    }

}
