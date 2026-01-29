using LotroKoniecDev.Domain.Core.Utilities;

namespace LotroKoniecDev.Tests.Integration.Core;

public class VarLenEncoderTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    public void Write_SingleByteValues_ShouldWriteOneByte(int value)
    {
        // Arrange
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        // Act
        VarLenEncoder.Write(writer, value);

        // Assert
        stream.ToArray().Should().HaveCount(1);
        stream.ToArray()[0].Should().Be((byte)value);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(255)]
    [InlineData(1000)]
    [InlineData(32767)]
    public void Write_TwoByteValues_ShouldWriteTwoBytes(int value)
    {
        // Arrange
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        // Act
        VarLenEncoder.Write(writer, value);

        // Assert
        stream.ToArray().Should().HaveCount(2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(255)]
    [InlineData(1000)]
    [InlineData(32767)]
    public void ReadWrite_RoundTrip_ShouldPreserveValue(int originalValue)
    {
        // Arrange
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        // Act - Write
        VarLenEncoder.Write(writer, originalValue);

        // Reset stream for reading
        stream.Position = 0;
        using var reader = new BinaryReader(stream);

        // Act - Read
        int readValue = VarLenEncoder.Read(reader);

        // Assert
        readValue.Should().Be(originalValue);
    }

    [Fact]
    public void Write_NegativeValue_ShouldThrow()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        // Act & Assert
        Action act = () => VarLenEncoder.Write(writer, -1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Write_ValueTooLarge_ShouldThrow()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        // Act & Assert
        Action act = () => VarLenEncoder.Write(writer, 32768);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Write_NullWriter_ShouldThrow()
    {
        // Act & Assert
        Action act = () => VarLenEncoder.Write(null!, 10);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Read_NullReader_ShouldThrow()
    {
        // Act & Assert
        Action act = () => VarLenEncoder.Read(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(127, 1)]
    [InlineData(128, 2)]
    [InlineData(32767, 2)]
    public void GetEncodedLength_ShouldReturnCorrectLength(int value, int expectedLength)
    {
        // Act
        int length = VarLenEncoder.GetEncodedLength(value);

        // Assert
        length.Should().Be(expectedLength);
    }

    [Fact]
    public void GetEncodedLength_NegativeValue_ShouldThrow()
    {
        // Act & Assert
        Action act = () => VarLenEncoder.GetEncodedLength(-1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
