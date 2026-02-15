using System.Text;
using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Tests.Unit.Tests.Models;

public sealed class SubFileTests
{
    [Theory]
    [InlineData(0x25000001, true)]  // Text file (0x25 high byte)
    [InlineData(0x25FFFFFF, true)]  // Text file
    [InlineData(0x24000001, false)] // Not a text file
    [InlineData(0x00000001, false)] // Not a text file
    [InlineData(0x26000001, false)] // Not a text file
    public void IsTextFile_ShouldReturnCorrectValue(int fileId, bool expected)
    {
        // Act
        bool result = SubFile.IsTextFile(fileId);

        // Assert
        result.ShouldBe(expected);
    }

    [Fact]
    public void Parse_NonTextFile_ShouldOnlyReadFileId()
    {
        // Arrange
        SubFile subFile = new();
        byte[] data = BitConverter.GetBytes(0x24000001); // Non-text file

        // Act
        subFile.Parse(data);

        // Assert
        subFile.IsText.ShouldBeFalse();
        subFile.FragmentCount.ShouldBe(0);
    }

    [Fact]
    public void Parse_NullData_ShouldThrowArgumentNullException()
    {
        // Arrange
        SubFile subFile = new();

        // Act
        Action action = () => subFile.Parse(null!);

        // Assert
        action.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public void Parse_TextFileWithNoFragments_ShouldParseCorrectly()
    {
        // Arrange
        SubFile subFile = new();
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        writer.Write(0x25000001); // File ID (text file)
        writer.Write(new byte[4]); // Unknown1
        writer.Write((byte)0);     // Unknown2
        writer.Write((byte)0);     // Num fragments (VarLen encoded, 0 = 1 byte)

        byte[] data = stream.ToArray();

        // Act
        subFile.Parse(data);

        // Assert
        subFile.IsText.ShouldBeTrue();
        subFile.FragmentCount.ShouldBe(0);
    }

    [Fact]
    public void Parse_TextFileWithFragment_ShouldParseFragments()
    {
        // Arrange
        SubFile subFile = new();
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        writer.Write(0x25000001); // File ID (text file)
        writer.Write(new byte[4]); // Unknown1
        writer.Write((byte)0);     // Unknown2
        writer.Write((byte)1);     // Num fragments = 1

        // Fragment data
        writer.Write((ulong)12345); // Fragment ID
        writer.Write(1);             // Num pieces = 1
        writer.Write((byte)4);       // Piece length = 4 (VarLen single byte)
        writer.Write(Encoding.Unicode.GetBytes("Test")); // Piece content
        writer.Write(0);             // Num arg refs = 0
        writer.Write((byte)0);       // Num arg string groups = 0

        byte[] data = stream.ToArray();

        // Act
        subFile.Parse(data);

        // Assert
        subFile.IsText.ShouldBeTrue();
        subFile.FragmentCount.ShouldBe(1);
        subFile.Fragments.ShouldContainKey(12345UL);
        subFile.Fragments[12345UL].GetFullText().ShouldBe("Test");
    }

    [Fact]
    public void TryGetFragment_ExistingFragment_ShouldReturnTrue()
    {
        // Arrange
        SubFile subFile = new();
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        writer.Write(0x25000001);
        writer.Write(new byte[4]);
        writer.Write((byte)0);
        writer.Write((byte)1);
        writer.Write((ulong)99999);
        writer.Write(1);
        writer.Write((byte)5);
        writer.Write(Encoding.Unicode.GetBytes("Hello"));
        writer.Write(0);
        writer.Write((byte)0);

        subFile.Parse(stream.ToArray());

        // Act
        bool found = subFile.TryGetFragment(99999, out Fragment? fragment);

        // Assert
        found.ShouldBeTrue();
        fragment.ShouldNotBeNull();
        fragment!.FragmentId.ShouldBe(99999UL);
    }

    [Fact]
    public void TryGetFragment_NonExistingFragment_ShouldReturnFalse()
    {
        // Arrange
        SubFile subFile = new();
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        writer.Write(0x25000001);
        writer.Write(new byte[4]);
        writer.Write((byte)0);
        writer.Write((byte)0); // No fragments

        subFile.Parse(stream.ToArray());

        // Act
        bool found = subFile.TryGetFragment(12345, out Fragment? fragment);

        // Assert
        found.ShouldBeFalse();
        fragment.ShouldBeNull();
    }

    [Fact]
    public void Serialize_ShouldProduceValidBinaryData()
    {
        // Arrange
        SubFile subFile = new();
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        writer.Write(0x25000001);
        writer.Write(new byte[4]);
        writer.Write((byte)0);
        writer.Write((byte)1);
        writer.Write((ulong)12345);
        writer.Write(1);
        writer.Write((byte)4);
        writer.Write(Encoding.Unicode.GetBytes("Test"));
        writer.Write(0);
        writer.Write((byte)0);

        byte[] originalData = stream.ToArray();
        subFile.Parse(originalData);

        // Act
        byte[] serialized = subFile.Serialize();

        // Assert
        serialized.ShouldNotBeEmpty();
        // Re-parse to verify
        SubFile reparsedSubFile = new();
        reparsedSubFile.Parse(serialized);
        reparsedSubFile.FragmentCount.ShouldBe(1);
        reparsedSubFile.Fragments[12345UL].GetFullText().ShouldBe("Test");
    }
}
