namespace LotroKoniecDev.Domain.Core.Utilities;

/// <summary>
/// Validates element counts read from binary data against the bytes remaining in the stream,
/// so a corrupt or crafted file fails fast with a clear parse error instead of driving
/// allocation loops with an impossible count.
/// </summary>
public static class BinaryBoundsGuard
{
    /// <summary>
    /// Ensures a declared element count can physically fit in the remaining stream bytes.
    /// </summary>
    /// <param name="reader">The binary reader (over a seekable stream) positioned after the count field.</param>
    /// <param name="count">The element count read from the data.</param>
    /// <param name="minBytesPerElement">The smallest valid encoded size of one element.</param>
    /// <param name="elementName">The element name used in the error message.</param>
    /// <exception cref="ArgumentNullException">When reader is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">When minBytesPerElement is not positive.</exception>
    /// <exception cref="InvalidDataException">When the count is negative or cannot fit in the remaining bytes.</exception>
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
