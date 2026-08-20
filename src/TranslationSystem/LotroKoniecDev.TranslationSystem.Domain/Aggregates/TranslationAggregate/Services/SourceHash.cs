using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;

/// <summary>
/// A 128-bit hash of a row's source triple (<c>Text</c>, <c>ArgsOrder</c>, <c>ArgsId</c>). The import
/// diff compares sources by hash instead of by string, so neither side keeps the text (spec 0006).
/// It is SHA-256 cut down to 128 bits. At the 2M-row design size the chance of a collision is about
/// 10⁻²⁶, which spec 0006 accepts: equal hash means equal source. Never stored in the database.
/// </summary>
/// <remarks>
/// <para>
/// Since ADR-0047 the top half also travels on the wire: <see cref="ToWireDigest"/> is the translation
/// file's seventh column, <c>source_digest</c>, and the patcher recomputes it from the fragment it is
/// about to overwrite. The framing below is therefore a contract between the two contexts, not an
/// internal detail. Each field is written as <c>marker | UTF-16 code-unit count (little-endian int32)
/// | UTF-16LE bytes</c>, and a missing field is the single marker byte <c>0</c>. That way <c>null</c>
/// never hashes like the empty string, and <c>("ab","c")</c> never collides with <c>("a","bc")</c>.
/// The patcher has its own copy (<c>LotroKoniecDev.Domain.Models.SourceDigest</c>); a golden fixture
/// on both sides keeps them identical (ADR-0047 §6).
/// </para>
/// </remarks>
public readonly record struct SourceHash(ulong High, ulong Low)
{
    /// <summary>Length in characters of the wire form: the first 8 digest bytes as hex.</summary>
    public const int WireDigestLength = 16;

    private const byte NullFieldMarker = 0;
    private const byte PresentFieldMarker = 1;
    private const int FieldHeaderSize = 5;
    private const int WireDigestBytes = WireDigestLength / 2;

    /// <summary>
    /// The <c>source_digest</c> column (ADR-0047 §2): the first eight <b>bytes</b> of the digest, in
    /// digest order, as lower-case hex. <see cref="High"/> holds those bytes read little-endian, so
    /// writing them back little-endian gives the same bytes. Formatting the <c>ulong</c> as <c>x16</c>
    /// would reverse them and quietly break parity with the patcher.
    /// 64 bits is enough on purpose: the check only asks whether the fragment still holds this exact
    /// English, so a wrong match needs the new English to hash to the old value (2⁻⁶⁴ per changed
    /// row). The full 128 bits would make every artifact about 30% larger.
    /// </summary>
    public string ToWireDigest()
    {
        Span<byte> bytes = stackalloc byte[WireDigestBytes];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, High);

        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static SourceHash Compute(TranslationSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return Compute(source.Text, source.ArgsOrder, source.ArgsId);
    }

    /// <summary>
    /// Whether <paramref name="value"/> can be a <c>source_digest</c> column: exactly 16 hex
    /// characters. This is how the carver tells a seventh column from a six-column line's
    /// <c>approved</c> field (ADR-0047 §2). Nothing else can sit in that slot, and hex never holds a
    /// <c>|</c>. Reading accepts upper case as well, so a hand-edited file still works; writers always
    /// emit lower case.
    /// </summary>
    public static bool IsWireDigest(string? value)
    {
        if (value is not { Length: WireDigestLength })
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The hash a patched DAT sends back when it is exported again (spec 0012): this row's current
    /// Polish text together with the source's own args columns. The patcher writes the text as it is
    /// and never changes the argument count, and the exporter rebuilds the args columns from that
    /// count, so an untouched fragment comes back as exactly this triple. It goes through
    /// <see cref="Compute(string, string?, string?)"/>, so it can be compared with an incoming source
    /// hash to hash. <c>null</c> when the row has no Polish: there is nothing of ours to come back.
    /// </summary>
    public static SourceHash? ComputeEcho(string? translatedText, string? argsOrder, string? argsId)
        => translatedText is null ? null : Compute(translatedText, argsOrder, argsId);

    /// <summary>
    /// Hashes the triple in the form it is stored in. Callers must pass the args columns the way
    /// <see cref="TranslationSource"/> keeps them: <c>null</c> when absent, never the literal text
    /// <c>NULL</c>. Otherwise the two sides of the diff would not agree.
    /// </summary>
    public static SourceHash Compute(string text, string? argsOrder, string? argsId)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Framing (spec 0006): each field is marker + char count + UTF-16 bytes, so a null never
        // hashes like an empty string and ("ab","c") never collides with ("a","bc").
        int requiredSize = FieldSize(text) + FieldSize(argsOrder) + FieldSize(argsId);
        byte[] rented = ArrayPool<byte>.Shared.Rent(requiredSize);
        try
        {
            Span<byte> framed = rented.AsSpan(0, requiredSize);
            int offset = WriteField(framed, text);
            offset += WriteField(framed[offset..], argsOrder);
            WriteField(framed[offset..], argsId);

            Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
            SHA256.HashData(framed, digest);

            return new SourceHash(
                BinaryPrimitives.ReadUInt64LittleEndian(digest),
                BinaryPrimitives.ReadUInt64LittleEndian(digest[8..]));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static int FieldSize(string? value)
        => value is null ? 1 : FieldHeaderSize + value.Length * sizeof(char);

    private static int WriteField(Span<byte> destination, string? value)
    {
        if (value is null)
        {
            destination[0] = NullFieldMarker;
            return 1;
        }

        destination[0] = PresentFieldMarker;
        BinaryPrimitives.WriteInt32LittleEndian(destination[1..], value.Length);

        ReadOnlySpan<byte> valueBytes = MemoryMarshal.AsBytes(value.AsSpan());
        valueBytes.CopyTo(destination[FieldHeaderSize..]);

        return FieldHeaderSize + valueBytes.Length;
    }
}
