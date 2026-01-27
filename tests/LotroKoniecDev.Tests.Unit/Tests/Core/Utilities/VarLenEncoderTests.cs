using LotroKoniecDev.Domain.Core.Utilities;

namespace LotroKoniecDev.Tests.Unit.Tests.Core.Utilities;

public sealed class VarLenEncoderTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    public void Read_SingleByteValue_ShouldReturnCorrectValue(int value)
    {
        // Arrange
        using var stream = new MemoryStream([(byte)value]);
        using var reader = new BinaryReader(stream);

        // Act
        var result = VarLenEncoder.Read(reader);

        // Assert
        result.Should().Be(value);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(1000)]
    [InlineData(32767)]
    public void Read_TwoByteValue_ShouldReturnCorrectValue(int value)
    {
        // Arrange
        var highByte = (byte)((value >> 8) | 0x80);
        var lowByte = (byte)(value & 0xFF);
        using var stream = new MemoryStream([highByte, lowByte]);
        using var reader = new BinaryReader(stream);

        // Act
        var result = VarLenEncoder.Read(reader);

        // Assert
        result.Should().Be(value);
    }

    [Fact]
    public void Read_NullReader_ShouldThrowArgumentNullException()
    {
        // Act
        var action = () => VarLenEncoder.Read(null!);

        // Assert
        action.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    public void Write_SingleByteValue_ShouldWriteOneByte(int value)
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
    [InlineData(1000)]
    [InlineData(32767)]
    public void Write_TwoByteValue_ShouldWriteTwoBytes(int value)
    {
        // Arrange
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        // Act
        VarLenEncoder.Write(writer, value);

        // Assert
        var bytes = stream.ToArray();
        bytes.Should().HaveCount(2);
        bytes[0].Should().Be((byte)((value >> 8) | 0x80));
        bytes[1].Should().Be((byte)(value & 0xFF));
    }

    [Fact]
    public void Write_NullWriter_ShouldThrowArgumentNullException()
    {
        // Act
        var action = () => VarLenEncoder.Write(null!, 42);

        // Assert
        action.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Write_NegativeValue_ShouldThrowArgumentOutOfRangeException()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        // Act
        var action = () => VarLenEncoder.Write(writer, -1);

        // Assert
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Write_ValueExceedsMaximum_ShouldThrowArgumentOutOfRangeException()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        // Act
        var action = () => VarLenEncoder.Write(writer, 32768);

        // Assert
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(127, 1)]
    [InlineData(128, 2)]
    [InlineData(32767, 2)]
    public void GetEncodedLength_ShouldReturnCorrectLength(int value, int expectedLength)
    {
        // Act
        var result = VarLenEncoder.GetEncodedLength(value);

        // Assert
        result.Should().Be(expectedLength);
    }

    [Fact]
    public void GetEncodedLength_NegativeValue_ShouldThrowArgumentOutOfRangeException()
    {
        // Act
        var action = () => VarLenEncoder.GetEncodedLength(-1);

        // Assert
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(1000)]
    [InlineData(32767)]
    public void ReadWrite_RoundTrip_ShouldPreserveValue(int value)
    {
        // Arrange
        using var writeStream = new MemoryStream();
        using var writer = new BinaryWriter(writeStream);
        VarLenEncoder.Write(writer, value);

        using var readStream = new MemoryStream(writeStream.ToArray());
        using var reader = new BinaryReader(readStream);

        // Act
        var result = VarLenEncoder.Read(reader);

        // Assert
        result.Should().Be(value);
    }
}
