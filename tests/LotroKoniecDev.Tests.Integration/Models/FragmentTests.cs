using System.Text;
using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Tests.Integration.Models;

public class FragmentTests
{
    [Fact]
    public void Parse_SimpleFragment_ShouldDeserializeCorrectly()
    {
        // Arrange
        using MemoryStream stream = new MemoryStream();
        using BinaryWriter writer = new BinaryWriter(stream);

        // Write fragment ID (ulong)
        writer.Write((ulong)12345);

        // Write pieces count and data
        writer.Write(2); // numPieces
        writer.Write((byte)5); // piece 1 length (varlen single byte)
        writer.Write(Encoding.Unicode.GetBytes("Hello")); // piece 1 data
        writer.Write((byte)5); // piece 2 length
        writer.Write(Encoding.Unicode.GetBytes("World")); // piece 2 data

        // Write arg refs count
        writer.Write(0); // numArgRefs

        // Write arg strings groups count
        writer.Write((byte)0); // numArgStringGroups

        stream.Position = 0;
        using BinaryReader reader = new BinaryReader(stream);

        // Act
        Fragment fragment = new Fragment();
        fragment.Parse(reader);

        // Assert
        fragment.FragmentId.Should().Be(12345UL);
        fragment.Pieces.Should().HaveCount(2);
        fragment.Pieces[0].Should().Be("Hello");
        fragment.Pieces[1].Should().Be("World");
        fragment.ArgRefs.Should().BeEmpty();
        fragment.ArgStrings.Should().BeEmpty();
        fragment.HasArguments.Should().BeFalse();
    }

    [Fact]
    public void Parse_FragmentWithArgRefs_ShouldDeserializeCorrectly()
    {
        // Arrange
        using MemoryStream stream = new MemoryStream();
        using BinaryWriter writer = new BinaryWriter(stream);

        writer.Write((ulong)999);
        writer.Write(1); // numPieces
        writer.Write((byte)4); // piece length
        writer.Write(Encoding.Unicode.GetBytes("Test"));
        writer.Write(2); // numArgRefs
        writer.Write(new byte[] { 1, 0, 0, 0 }); // argRef 1
        writer.Write(new byte[] { 2, 0, 0, 0 }); // argRef 2
        writer.Write((byte)0); // numArgStringGroups

        stream.Position = 0;
        using BinaryReader reader = new BinaryReader(stream);

        // Act
        Fragment fragment = new Fragment();
        fragment.Parse(reader);

        // Assert
        fragment.HasArguments.Should().BeTrue();
        fragment.ArgRefs.Should().HaveCount(2);
    }

    [Fact]
    public void Write_Fragment_ShouldSerializeCorrectly()
    {
        // Arrange
        Fragment fragment = new Fragment
        {
            Pieces = ["Hello", "World"]
        };

        using MemoryStream stream = new MemoryStream();
        using BinaryWriter writer = new BinaryWriter(stream);

        // Act
        fragment.Write(writer);

        // Assert - verify can be read back
        stream.Position = 0;
        using BinaryReader reader = new BinaryReader(stream);

        Fragment parsedFragment = new Fragment();
        parsedFragment.Parse(reader);

        parsedFragment.Pieces.Should().BeEquivalentTo(fragment.Pieces);
    }

    [Fact]
    public void GetFullText_WithSeparator_ShouldJoinPieces()
    {
        // Arrange
        Fragment fragment = new Fragment
        {
            Pieces = ["Hello", "World", "!"]
        };

        // Act
        string result = fragment.GetFullText(" ");

        // Assert
        result.Should().Be("Hello World !");
    }

    [Fact]
    public void Parse_NullReader_ShouldThrow()
    {
        // Arrange
        Fragment fragment = new Fragment();

        // Act & Assert
        Action act = () => fragment.Parse(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Write_NullWriter_ShouldThrow()
    {
        // Arrange
        Fragment fragment = new Fragment();

        // Act & Assert
        Action act = () => fragment.Write(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void RoundTrip_ComplexFragment_ShouldPreserveData()
    {
        // Arrange
        Fragment original = new Fragment
        {
            Pieces = ["Part1", "Part2", "Part3"]
        };

        using MemoryStream stream = new MemoryStream();
        using BinaryWriter writer = new BinaryWriter(stream);

        // Act
        original.Write(writer);
        stream.Position = 0;

        using BinaryReader reader = new BinaryReader(stream);
        Fragment restored = new Fragment();
        restored.Parse(reader);

        // Assert
        restored.Pieces.Should().BeEquivalentTo(original.Pieces);
    }
}
