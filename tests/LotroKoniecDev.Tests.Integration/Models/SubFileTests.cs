using System.Text;
using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Tests.Integration.Models;

public class SubFileTests
{
    private const int TextFileId = 0x25000001; // Valid text file ID (0x25 high byte)
    private const int NonTextFileId = 0x10000001; // Non-text file ID

    [Fact]
    public void IsTextFile_WithTextFileId_ShouldReturnTrue()
    {
        // Act
        bool result = SubFile.IsTextFile(TextFileId);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsTextFile_WithNonTextFileId_ShouldReturnFalse()
    {
        // Act
        bool result = SubFile.IsTextFile(NonTextFileId);

        // Assert
        result.Should().BeFalse();
    }

    [Theory]
    [InlineData(0x25000000, true)]
    [InlineData(0x25FFFFFF, true)]
    [InlineData(0x24FFFFFF, false)]
    [InlineData(0x26000000, false)]
    [InlineData(0x00000000, false)]
    public void IsTextFile_VariousIds_ShouldReturnExpected(int fileId, bool expected)
    {
        // Act
        bool result = SubFile.IsTextFile(fileId);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void Parse_TextFile_ShouldDeserializeCorrectly()
    {
        // Arrange
        byte[] data = CreateTextSubFileData(TextFileId, 1);

        // Act
        SubFile subFile = new SubFile();
        subFile.Parse(data);

        // Assert
        subFile.FileId.Should().Be(TextFileId);
        subFile.IsText.Should().BeTrue();
        subFile.Fragments.Should().HaveCount(1);
        subFile.FragmentCount.Should().Be(1);
    }

    [Fact]
    public void Parse_NonTextFile_ShouldNotParseFragments()
    {
        // Arrange
        using MemoryStream stream = new MemoryStream();
        using BinaryWriter writer = new BinaryWriter(stream);

        writer.Write(NonTextFileId);
        writer.Write(new byte[100]); // Some data

        byte[] data = stream.ToArray();

        // Act
        SubFile subFile = new SubFile();
        subFile.Parse(data);

        // Assert
        subFile.FileId.Should().Be(NonTextFileId);
        subFile.IsText.Should().BeFalse();
        subFile.Fragments.Should().BeEmpty();
    }

    [Fact]
    public void TryGetFragment_ExistingFragment_ShouldReturnTrue()
    {
        // Arrange
        byte[] data = CreateTextSubFileData(TextFileId, 1, fragmentId: 12345);
        SubFile subFile = new SubFile();
        subFile.Parse(data);

        // Act
        bool found = subFile.TryGetFragment(12345, out Fragment? fragment);

        // Assert
        found.Should().BeTrue();
        fragment.Should().NotBeNull();
        fragment!.FragmentId.Should().Be(12345UL);
    }

    [Fact]
    public void TryGetFragment_NonExistingFragment_ShouldReturnFalse()
    {
        // Arrange
        byte[] data = CreateTextSubFileData(TextFileId, 1, fragmentId: 12345);
        SubFile subFile = new SubFile();
        subFile.Parse(data);

        // Act
        bool found = subFile.TryGetFragment(99999, out Fragment? fragment);

        // Assert
        found.Should().BeFalse();
        fragment.Should().BeNull();
    }

    [Fact]
    public void Serialize_ShouldCreateValidBinaryData()
    {
        // Arrange
        byte[] originalData = CreateTextSubFileData(TextFileId, 2);
        SubFile subFile = new SubFile();
        subFile.Parse(originalData);

        // Act
        byte[] serialized = subFile.Serialize();

        // Parse again to verify
        SubFile restored = new SubFile();
        restored.Parse(serialized);

        // Assert
        restored.FileId.Should().Be(subFile.FileId);
        restored.FragmentCount.Should().Be(subFile.FragmentCount);
    }

    [Fact]
    public void Parse_NullData_ShouldThrow()
    {
        // Arrange
        SubFile subFile = new SubFile();

        // Act & Assert
        Action act = () => subFile.Parse(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Version_ShouldBeSettable()
    {
        // Arrange
        SubFile subFile = new SubFile { Version = 42 };

        // Assert
        subFile.Version.Should().Be(42);
    }

    private static byte[] CreateTextSubFileData(int fileId, int fragmentCount, ulong fragmentId = 1)
    {
        using MemoryStream stream = new MemoryStream();
        using BinaryWriter writer = new BinaryWriter(stream);

        writer.Write(fileId);
        writer.Write(new byte[4]); // Unknown1
        writer.Write((byte)0); // Unknown2
        writer.Write((byte)fragmentCount); // numFragments (varlen single byte)

        for (int i = 0; i < fragmentCount; i++)
        {
            // Fragment ID
            writer.Write(fragmentId + (ulong)i);

            // Pieces
            writer.Write(1); // numPieces
            writer.Write((byte)4); // piece length (varlen)
            writer.Write(Encoding.Unicode.GetBytes("Test"));

            // ArgRefs
            writer.Write(0); // numArgRefs

            // ArgStrings
            writer.Write((byte)0); // numArgStringGroups
        }

        return stream.ToArray();
    }
}
