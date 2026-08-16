using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;

/// <summary>
/// A 128-bit hash of a row's source triple (<c>Text</c>, <c>ArgsOrder</c>, <c>ArgsId</c>) — the
/// import diff's equality unit (spec 0006): incoming and stored sources are compared hash-to-hash
/// so neither side ever retains the strings. SHA-256 truncated to 128 bits; collision odds at the
/// 2M-row design horizon are ~10⁻²⁶, accepted in spec 0006 — the diff treats hash-equal as
/// source-identical. Never persisted, so the encoding only has to be self-consistent within one
/// process.
/// </summary>
public readonly record struct SourceHash(ulong High, ulong Low)
{
    private const byte NullFieldMarker = 0;
    private const byte PresentFieldMarker = 1;
    private const int FieldHeaderSize = 5;

    public static SourceHash Compute(TranslationSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return Compute(source.Text, source.ArgsOrder, source.ArgsId);
    }

    /// <summary>
    /// The hash a DAT patched with this row's Polish echoes back when it is exported again (spec
    /// 0012): the current Polish text framed with the source's own args columns — the patcher writes
    /// the text verbatim and never changes the argument count, and the exporter re-emits the args
    /// columns from that count, so an unchanged fragment comes back as exactly this triple. Hashed
    /// through <see cref="Compute(string, string?, string?)"/> so it compares against an incoming
    /// source hash-to-hash. <c>null</c> when the row carries no Polish — nothing of ours can echo.
    /// </summary>
    public static SourceHash? ComputeEcho(string? translatedText, string? argsOrder, string? argsId)
        => translatedText is null ? null : Compute(translatedText, argsOrder, argsId);

    /// <summary>
    /// Hashes the triple in its persisted (value-object-normalized) representation — callers must
    /// pass args columns the way <see cref="TranslationSource"/> stores them (<c>null</c> when
    /// absent, never the raw <c>NULL</c> literal), or the two sides of the diff would disagree.
    /// </summary>
    public static SourceHash Compute(string text, string? argsOrder, string? argsId)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Length/null framing (spec 0006): each field is marker + char count + UTF-16 bytes, so a
        // null never equals an empty string and ("ab","c") never collides with ("a","bc").
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
