namespace LotroKoniecDev.Domain.Core.Utilities;

/// <summary>
/// Provides variable-length integer encoding/decoding utilities.
/// Values 0-127 use 1 byte, values 128-32767 use 2 bytes.
/// </summary>
public static class VarLenEncoder
{
    private const int HighBitMask = 0x80;
    private const int LowByteMask = 0xFF;
    private const int MaxSingleByteValue = 0x7F;
    private const int MaxTwoByteValue = 0x7FFF;

    /// <summary>
    /// Reads a variable-length encoded integer from a BinaryReader.
    /// </summary>
    /// <param name="reader">The binary reader to read from.</param>
    /// <returns>The decoded integer value.</returns>
    /// <exception cref="ArgumentNullException">When reader is null.</exception>
    public static int Read(BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        int value = reader.ReadByte();

        if ((value & HighBitMask) != 0)
        {
            value = ((value ^ HighBitMask) << 8) | reader.ReadByte();
        }

        return value;
    }

    /// <summary>
    /// Writes a variable-length encoded integer to a BinaryWriter.
    /// </summary>
    /// <param name="writer">The binary writer to write to.</param>
    /// <param name="value">The integer value to encode (0-32767).</param>
    /// <exception cref="ArgumentNullException">When writer is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">When value is negative or exceeds maximum.</exception>
    public static void Write(BinaryWriter writer, int value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaxTwoByteValue);

        if (value > MaxSingleByteValue)
        {
            writer.Write((byte)((value >> 8) | HighBitMask));
            writer.Write((byte)(value & LowByteMask));
        }
        else
        {
            writer.Write((byte)value);
        }
    }

    /// <summary>
    /// Gets the number of bytes required to encode the given value.
    /// </summary>
    /// <param name="value">The value to check.</param>
    /// <returns>1 for values 0-127, 2 for values 128-32767.</returns>
    public static int GetEncodedLength(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        return value > MaxSingleByteValue ? 2 : 1;
    }
}
