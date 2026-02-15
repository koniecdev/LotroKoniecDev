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
        using MemoryStream stream = new([(byte)value]);
        using BinaryReader reader = new(stream);

        // Act
        int result = VarLenEncoder.Read(reader);

        // Assert
        result.ShouldBe(value);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(1000)]
    [InlineData(32767)]
    public void Read_TwoByteValue_ShouldReturnCorrectValue(int value)
    {
        // Arrange
        byte highByte = (byte)((value >> 8) | 0x80);
        byte lowByte = (byte)(value & 0xFF);
        using MemoryStream stream = new([highByte, lowByte]);
        using BinaryReader reader = new(stream);

        // Act
        int result = VarLenEncoder.Read(reader);

        // Assert
        result.ShouldBe(value);
    }

    [Fact]
    public void Read_NullReader_ShouldThrowArgumentNullException()
    {
        // Act
        Action action = () => VarLenEncoder.Read(null!);

        // Assert
        action.ShouldThrow<ArgumentNullException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    public void Write_SingleByteValue_ShouldWriteOneByte(int value)
    {
        // Arrange
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        // Act
        VarLenEncoder.Write(writer, value);

        // Assert
        stream.ToArray().Length.ShouldBe(1);
        stream.ToArray()[0].ShouldBe((byte)value);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(1000)]
    [InlineData(32767)]
    public void Write_TwoByteValue_ShouldWriteTwoBytes(int value)
    {
        // Arrange
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        // Act
        VarLenEncoder.Write(writer, value);

        // Assert
        byte[] bytes = stream.ToArray();
        bytes.Length.ShouldBe(2);
        bytes[0].ShouldBe((byte)((value >> 8) | 0x80));
        bytes[1].ShouldBe((byte)(value & 0xFF));
    }

    [Fact]
    public void Write_NullWriter_ShouldThrowArgumentNullException()
    {
        // Act
        Action action = () => VarLenEncoder.Write(null!, 42);

        // Assert
        action.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public void Write_NegativeValue_ShouldThrowArgumentOutOfRangeException()
    {
        // Arrange
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        // Act
        Action action = () => VarLenEncoder.Write(writer, -1);

        // Assert
        action.ShouldThrow<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Write_ValueExceedsMaximum_ShouldThrowArgumentOutOfRangeException()
    {
        // Arrange
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        // Act
        Action action = () => VarLenEncoder.Write(writer, 32768);

        // Assert
        action.ShouldThrow<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(127, 1)]
    [InlineData(128, 2)]
    [InlineData(32767, 2)]
    public void GetEncodedLength_ShouldReturnCorrectLength(int value, int expectedLength)
    {
        // Act
        int result = VarLenEncoder.GetEncodedLength(value);

        // Assert
        result.ShouldBe(expectedLength);
    }

    [Fact]
    public void GetEncodedLength_NegativeValue_ShouldThrowArgumentOutOfRangeException()
    {
        // Act
        Action action = () => VarLenEncoder.GetEncodedLength(-1);

        // Assert
        action.ShouldThrow<ArgumentOutOfRangeException>();
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
        using MemoryStream writeStream = new();
        using BinaryWriter writer = new(writeStream);
        VarLenEncoder.Write(writer, value);

        using MemoryStream readStream = new(writeStream.ToArray());
        using BinaryReader reader = new(readStream);

        // Act
        int result = VarLenEncoder.Read(reader);

        // Assert
        result.ShouldBe(value);
    }
}
