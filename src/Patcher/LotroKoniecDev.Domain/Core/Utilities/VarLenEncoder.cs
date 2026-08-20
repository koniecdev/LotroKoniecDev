using LotroKoniecDev.Primitives.Constants;

namespace LotroKoniecDev.Domain.Core.Utilities;

/// <summary>
/// Reads and writes the DAT's variable-length integers: 0-127 fit in one byte, 128-32767 take two.
/// </summary>
public static class VarLenEncoder
{
    private const int HighBitMask = 0x80;
    private const int LowByteMask = 0xFF;
    private const int MaxSingleByteValue = 0x7F;

    // The same constant Fragment.IsWritablePiece uses. If the two disagreed, #598 would come back:
    // we would either write a piece that does not fit, or refuse one that does.
    private const int MaxTwoByteValue = DatFileConstants.MaxVarLenValue;

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

    /// <param name="value">The value to encode. It must be between 0 and 32767.</param>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or above the maximum.</exception>
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

    /// <returns>1 for values 0-127, 2 for values 128-32767.</returns>
    public static int GetEncodedLength(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        return value > MaxSingleByteValue ? 2 : 1;
    }
}
