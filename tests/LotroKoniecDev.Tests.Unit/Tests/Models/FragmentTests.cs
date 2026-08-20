using System.Text;
using LotroKoniecDev.Domain.Core.Utilities;
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

    [Fact]
    public void Parse_WithArgStrings_ShouldParseArgStringGroups()
    {
        // Arrange: fragment with 1 piece, 0 arg refs, 2 arg string groups
        Fragment fragment = new();
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        writer.Write((ulong)42);
        writer.Write(1); // 1 piece
        writer.Write((byte)3);
        writer.Write(Encoding.Unicode.GetBytes("Hey"));
        writer.Write(0); // 0 arg refs
        writer.Write((byte)2); // 2 arg string groups

        // Group 1: 2 strings
        writer.Write(2);
        writer.Write((byte)3);
        writer.Write(Encoding.Unicode.GetBytes("Foo"));
        writer.Write((byte)3);
        writer.Write(Encoding.Unicode.GetBytes("Bar"));

        // Group 2: 1 string
        writer.Write(1);
        writer.Write((byte)3);
        writer.Write(Encoding.Unicode.GetBytes("Baz"));

        stream.Position = 0;
        using BinaryReader reader = new(stream);

        // Act
        fragment.Parse(reader);

        // Assert
        fragment.ArgStrings.Count.ShouldBe(2);
        fragment.ArgStrings[0].ShouldBe(new[] { "Foo", "Bar" });
        fragment.ArgStrings[1].ShouldBe(new[] { "Baz" });
    }

    [Fact]
    public void TryReorderArgRefs_NullOrder_ShouldThrowArgumentNullException()
    {
        // Arrange
        Fragment fragment = new();
        fragment.ArgRefs.Add([0x01, 0x00, 0x00, 0x00]);

        // Act
        Action action = () => fragment.TryReorderArgRefs(null!);

        // Assert
        action.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public void TryReorderArgRefs_SwapTwoArgs_ShouldReorder()
    {
        // Arrange
        Fragment fragment = new();
        fragment.ArgRefs.Add([0x01, 0x00, 0x00, 0x00]);
        fragment.ArgRefs.Add([0x02, 0x00, 0x00, 0x00]);

        // Act
        bool result = fragment.TryReorderArgRefs([1, 0]);

        // Assert
        result.ShouldBeTrue();
        fragment.ArgRefs[0].ShouldBe(new byte[] { 0x02, 0x00, 0x00, 0x00 });
        fragment.ArgRefs[1].ShouldBe(new byte[] { 0x01, 0x00, 0x00, 0x00 });
    }

    [Fact]
    public void TryReorderArgRefs_SameOrder_ShouldPreserveOrder()
    {
        // Arrange
        Fragment fragment = new();
        fragment.ArgRefs.Add([0x01, 0x00, 0x00, 0x00]);
        fragment.ArgRefs.Add([0x02, 0x00, 0x00, 0x00]);

        // Act
        bool result = fragment.TryReorderArgRefs([0, 1]);

        // Assert
        result.ShouldBeTrue();
        fragment.ArgRefs[0].ShouldBe(new byte[] { 0x01, 0x00, 0x00, 0x00 });
        fragment.ArgRefs[1].ShouldBe(new byte[] { 0x02, 0x00, 0x00, 0x00 });
    }

    [Fact]
    public void TryReorderArgRefs_MismatchedLength_ShouldReturnFalse()
    {
        // Arrange
        Fragment fragment = new();
        fragment.ArgRefs.Add([0x01, 0x00, 0x00, 0x00]);
        fragment.ArgRefs.Add([0x02, 0x00, 0x00, 0x00]);

        // Act
        bool result = fragment.TryReorderArgRefs([0, 1, 2]);

        // Assert
        result.ShouldBeFalse();
        // ArgRefs should remain unchanged
        fragment.ArgRefs.Count.ShouldBe(2);
    }

    [Fact]
    public void TryReorderArgRefs_OutOfRangeIndex_ShouldReturnFalse()
    {
        // Arrange
        Fragment fragment = new();
        fragment.ArgRefs.Add([0x01, 0x00, 0x00, 0x00]);
        fragment.ArgRefs.Add([0x02, 0x00, 0x00, 0x00]);

        // Act
        bool result = fragment.TryReorderArgRefs([0, 5]);

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public void TryReorderArgRefs_ThreeArgs_ShouldReorderCorrectly()
    {
        // Arrange
        Fragment fragment = new();
        fragment.ArgRefs.Add([0x0A, 0x00, 0x00, 0x00]);
        fragment.ArgRefs.Add([0x0B, 0x00, 0x00, 0x00]);
        fragment.ArgRefs.Add([0x0C, 0x00, 0x00, 0x00]);

        // Act: rotate: [2, 0, 1] means new[0]=old[2], new[1]=old[0], new[2]=old[1]
        bool result = fragment.TryReorderArgRefs([2, 0, 1]);

        // Assert
        result.ShouldBeTrue();
        fragment.ArgRefs[0].ShouldBe(new byte[] { 0x0C, 0x00, 0x00, 0x00 });
        fragment.ArgRefs[1].ShouldBe(new byte[] { 0x0A, 0x00, 0x00, 0x00 });
        fragment.ArgRefs[2].ShouldBe(new byte[] { 0x0B, 0x00, 0x00, 0x00 });
    }

    [Fact]
    public void ParseAndWrite_RoundTrip_WithArgStrings_ShouldPreserveData()
    {
        // Arrange: build binary with pieces + arg refs + arg string groups
        using MemoryStream originalStream = new();
        using BinaryWriter originalWriter = new(originalStream);

        originalWriter.Write((ulong)555);
        originalWriter.Write(1); // 1 piece
        originalWriter.Write((byte)4);
        originalWriter.Write(Encoding.Unicode.GetBytes("Text"));
        originalWriter.Write(1); // 1 arg ref
        originalWriter.Write(new byte[] { 0x01, 0x00, 0x00, 0x00 });
        originalWriter.Write((byte)1); // 1 arg string group
        originalWriter.Write(2); // group has 2 strings
        originalWriter.Write((byte)5);
        originalWriter.Write(Encoding.Unicode.GetBytes("Alpha"));
        originalWriter.Write((byte)4);
        originalWriter.Write(Encoding.Unicode.GetBytes("Beta"));

        byte[] originalData = originalStream.ToArray();

        // Parse
        Fragment parsed = new();
        using MemoryStream parseStream = new(originalData);
        using BinaryReader parseReader = new(parseStream);
        parsed.Parse(parseReader);

        // Write back
        using MemoryStream writeStream = new();
        using BinaryWriter writeWriter = new(writeStream);
        parsed.Write(writeWriter);

        // Re-parse and verify
        Fragment reparsed = new();
        using MemoryStream reparseStream = new(writeStream.ToArray());
        using BinaryReader reparseReader = new(reparseStream);
        reparsed.Parse(reparseReader);

        // Assert
        reparsed.FragmentId.ShouldBe(555UL);
        reparsed.Pieces.ShouldBe(new[] { "Text" });
        reparsed.ArgRefs.Count.ShouldBe(1);
        reparsed.ArgStrings.Count.ShouldBe(1);
        reparsed.ArgStrings[0].ShouldBe(new[] { "Alpha", "Beta" });
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(1000)]
    [InlineData(-1)]
    public void Parse_ImpossiblePieceCount_ShouldThrowInvalidDataException(int declaredPieces)
    {
        // Arrange: declares more pieces than the remaining bytes could ever hold
        Fragment fragment = new();
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        writer.Write((ulong)100);
        writer.Write(declaredPieces);

        stream.Position = 0;
        using BinaryReader reader = new(stream);

        // Act
        Action action = () => fragment.Parse(reader);

        // Assert
        action.ShouldThrow<InvalidDataException>();
    }

    [Fact]
    public void Parse_PieceLengthExceedingRemainingBytes_ShouldThrowInvalidDataException()
    {
        // Arrange: the piece declares 300 characters (600 bytes) but only 4 bytes follow
        Fragment fragment = new();
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        writer.Write((ulong)100);
        writer.Write(1); // Num pieces = 1
        VarLenEncoder.Write(writer, 300);
        writer.Write(Encoding.Unicode.GetBytes("Hi"));

        stream.Position = 0;
        using BinaryReader reader = new(stream);

        // Act
        Action action = () => fragment.Parse(reader);

        // Assert
        action.ShouldThrow<InvalidDataException>();
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(1000)]
    [InlineData(-1)]
    public void Parse_ImpossibleArgRefCount_ShouldThrowInvalidDataException(int declaredArgRefs)
    {
        // Arrange: a valid empty piece list, then an arg-ref count with no data behind it
        Fragment fragment = new();
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        writer.Write((ulong)100);
        writer.Write(0); // Num pieces
        writer.Write(declaredArgRefs);

        stream.Position = 0;
        using BinaryReader reader = new(stream);

        // Act
        Action action = () => fragment.Parse(reader);

        // Assert
        action.ShouldThrow<InvalidDataException>();
    }

    [Fact]
    public void Parse_ArgRefCountLargerThanRemainingBytes_ShouldThrowInvalidDataException()
    {
        // Arrange: declares 2 arg refs (8 bytes) but only one 4-byte ref is present
        Fragment fragment = new();
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        writer.Write((ulong)100);
        writer.Write(0); // Num pieces
        writer.Write(2); // Num arg refs = 2
        writer.Write(new byte[] { 0x01, 0x02, 0x03, 0x04 });

        stream.Position = 0;
        using BinaryReader reader = new(stream);

        // Act
        Action action = () => fragment.Parse(reader);

        // Assert
        action.ShouldThrow<InvalidDataException>();
    }

    [Fact]
    public void Parse_ArgStringGroupCountExceedingRemainingBytes_ShouldThrowInvalidDataException()
    {
        // Arrange: declares 255 arg string groups with no group data behind the count
        Fragment fragment = new();
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        writer.Write((ulong)100);
        writer.Write(0); // Num pieces
        writer.Write(0); // Num arg refs
        writer.Write((byte)255);

        stream.Position = 0;
        using BinaryReader reader = new(stream);

        // Act
        Action action = () => fragment.Parse(reader);

        // Assert
        action.ShouldThrow<InvalidDataException>();
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(1000)]
    [InlineData(-1)]
    public void Parse_ImpossibleArgStringCount_ShouldThrowInvalidDataException(int declaredStrings)
    {
        // Arrange: one arg string group whose string count cannot fit in the remaining bytes
        Fragment fragment = new();
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        writer.Write((ulong)100);
        writer.Write(0); // Num pieces
        writer.Write(0); // Num arg refs
        writer.Write((byte)1); // 1 arg string group
        writer.Write(declaredStrings);

        stream.Position = 0;
        using BinaryReader reader = new(stream);

        // Act
        Action action = () => fragment.Parse(reader);

        // Assert
        action.ShouldThrow<InvalidDataException>();
    }

    [Fact]
    public void Parse_ArgStringLengthExceedingRemainingBytes_ShouldThrowInvalidDataException()
    {
        // Arrange: the arg string declares 300 characters (600 bytes) but only 4 bytes follow
        Fragment fragment = new();
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        writer.Write((ulong)100);
        writer.Write(0); // Num pieces
        writer.Write(0); // Num arg refs
        writer.Write((byte)1); // 1 arg string group
        writer.Write(1); // group has 1 string
        VarLenEncoder.Write(writer, 300);
        writer.Write(Encoding.Unicode.GetBytes("Hi"));

        stream.Position = 0;
        using BinaryReader reader = new(stream);

        // Act
        Action action = () => fragment.Parse(reader);

        // Assert
        action.ShouldThrow<InvalidDataException>();
    }
}
