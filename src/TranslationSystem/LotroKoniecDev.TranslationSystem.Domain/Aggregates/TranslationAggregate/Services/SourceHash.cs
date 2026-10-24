using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;

/// <summary>
/// SHA-256 of a row's source triple (<c>Text</c>, <c>ArgsOrder</c>, <c>ArgsId</c>), cut to 128 bits.
/// The import diff compares sources by hash, so neither side keeps the text (spec 0006). At 2M rows a
/// collision is about 10⁻²⁶, so equal hash means equal source. Never stored in the database.
/// </summary>
/// <remarks>
/// The framing is a contract with the patcher, which recomputes <see cref="ToWireDigest"/> from the
/// fragment it is about to overwrite (ADR-0047). Each field is <c>marker | UTF-16 code-unit count
/// (int32 LE) | UTF-16LE bytes</c>, and a missing field is the single byte <c>0</c>, so <c>null</c> never
/// hashes like <c>""</c> and <c>("ab","c")</c> never collides with <c>("a","bc")</c>. The patcher's copy
/// is <c>LotroKoniecDev.Domain.Models.SourceDigest</c>; golden fixtures on both sides keep them equal
/// (ADR-0047 §6).
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
    /// The <c>source_digest</c> column (ADR-0047 §2): the first eight digest bytes, in digest order, as
    /// lower-case hex. Formatting <see cref="High"/> as <c>x16</c> would reverse the bytes and break
    /// parity with the patcher. 64 bits is enough to ask "is this still the same English" (2⁻⁶⁴ per
    /// changed row); 128 would make every artifact about 30% larger.
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
    /// Exactly 16 hex characters. This is how the carver tells a seventh column from a six-column
    /// line's <c>approved</c> field (ADR-0047 §2). Upper case is accepted so a hand-edited file still
    /// works; writers always emit lower case.
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
    /// The hash a patched DAT sends back when it is exported again (spec 0012): the row's Polish with the
    /// source's args columns. The patcher never changes the argument count and the exporter rebuilds the
    /// args columns from it, so an untouched fragment comes back as exactly this triple.
    /// <c>null</c> when the row has no Polish.
    /// </summary>
    public static SourceHash? ComputeEcho(string? translatedText, string? argsOrder, string? argsId)
        => translatedText is null ? null : Compute(translatedText, argsOrder, argsId);

    /// <summary>
    /// Pass the args columns the way <see cref="TranslationSource"/> keeps them: <c>null</c> when
    /// absent, never the literal text <c>NULL</c>. Otherwise the two sides of the diff disagree.
    /// </summary>
    public static SourceHash Compute(string text, string? argsOrder, string? argsId)
    {
        ArgumentNullException.ThrowIfNull(text);

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
