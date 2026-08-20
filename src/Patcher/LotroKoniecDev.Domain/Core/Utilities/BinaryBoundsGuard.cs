namespace LotroKoniecDev.Domain.Core.Utilities;

/// <summary>
/// Checks an element count read from binary data against the bytes still left in the stream. A
/// corrupt or hand-made file then fails at once with a clear parse error, instead of sending an
/// impossible count into an allocation loop.
/// </summary>
public static class BinaryBoundsGuard
{
    /// <param name="reader">A reader over a seekable stream, placed just after the count field.</param>
    /// <param name="count">The element count read from the data.</param>
    /// <param name="minBytesPerElement">The smallest number of bytes one element can take.</param>
    /// <param name="elementName">The name used in the error message.</param>
    /// <exception cref="InvalidDataException">The count is negative, or too large for the bytes left.</exception>
    public static void EnsureCountFits(BinaryReader reader, int count, int minBytesPerElement, string elementName)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minBytesPerElement);

        if (count < 0)
        {
            throw new InvalidDataException($"Declared {elementName} count is negative: {count}.");
        }

        long remaining = reader.BaseStream.Length - reader.BaseStream.Position;
        long required = (long)count * minBytesPerElement;

        if (required > remaining)
        {
            throw new InvalidDataException(
                $"Declared {elementName} count {count} requires at least {required} bytes, but only {remaining} remain.");
        }
    }
}
