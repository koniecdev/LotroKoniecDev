using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LotroKoniecDev.Primitives.Constants;

namespace LotroKoniecDev.Domain.Models;

/// <summary>
/// The patcher's half of the <c>source_digest</c> contract (ADR-0047): 16 lowercase hex characters
/// identifying the export-form triple <c>(text, args_order, args_id)</c> a fragment holds. The TMS
/// computes the same value from its stored <c>TranslationSource</c> (its <c>SourceHash</c>), ships it
/// as the translation file's seventh column, and the patcher recomputes it from the loaded fragment
/// to decide whether a row may be written over what the DAT currently holds.
/// </summary>
/// <remarks>
/// <para>
/// <b>Framing.</b> SHA-256 over the three fields concatenated as <c>marker | UTF-16 code-unit count
/// (little-endian int32) | UTF-16LE bytes</c>, where an absent field is the single marker byte
/// <c>0</c> and a present one is <c>1</c> plus its header and bytes. The framing is what keeps
/// <c>null</c> distinct from the empty string and <c>("ab","c")</c> distinct from <c>("a","bc")</c>.
/// The wire value is the first eight <b>bytes</b> of the digest in digest order, hex-encoded and
/// lower-cased — never a <c>ulong</c>'s <c>x16</c> rendering, which is the opposite byte order.
/// </para>
/// <para>
/// <b>Duplicated by design.</b> The two bounded contexts share the file, never code (CLAUDE.md), so
/// this is an independent implementation of the TMS' <c>SourceHash</c>. A golden fixture pinned by a
/// unit test in both contexts is what stops the copies drifting (ADR-0047 §6) — drift would fail a
/// build instead of an update day.
/// </para>
/// </remarks>
public static class SourceDigest
{
    /// <summary>Length, in characters, of the wire form — the first 8 digest bytes as hex.</summary>
    public const int WireLength = 16;

    private const byte NullFieldMarker = 0;
    private const byte PresentFieldMarker = 1;
    private const int FieldHeaderSize = 5;
    private const int WireBytes = WireLength / 2;
    private const char ArgsPositionSeparator = '-';

    /// <summary>
    /// The digest of what <paramref name="fragment"/> currently holds, composed exactly the way
    /// <c>export</c> writes it: the pieces joined with the placeholder, and identity argument columns
    /// derived from the fragment's own argument-reference count.
    /// </summary>
    public static string ForFragment(Fragment fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);

        return ForExportForm(
            string.Join(DatFileConstants.PieceSeparator, fragment.Pieces),
            fragment.ArgRefs.Count);
    }

    /// <summary>
    /// The digest of <paramref name="text"/> as the exporter would frame it for a fragment carrying
    /// <paramref name="argumentCount"/> argument references — identity args <c>1-2-…-n</c>, or absent
    /// when the fragment carries none. Used for the fragment's current content and, with a row's
    /// translated text, for the digest that row would leave behind once written.
    /// </summary>
    public static string ForExportForm(string text, int argumentCount)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegative(argumentCount);

        string? identityArgs = IdentityArgs(argumentCount);

        return Compute(text, identityArgs, identityArgs);
    }

    /// <summary>
    /// Hashes the triple in its value-object form — an absent args column is <see langword="null"/>,
    /// never the file's <c>NULL</c> literal, or the two contexts would disagree.
    /// </summary>
    public static string Compute(string text, string? argsOrder, string? argsId)
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

            return Convert.ToHexString(digest[..WireBytes]).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Whether <paramref name="value"/> can be a wire digest — exactly 16 hex characters. This is
    /// what lets both carvers tell a seventh column from a six-column line's <c>approved</c> field
    /// (ADR-0047 §2): nothing else can occupy that slot, and hex can never hold a <c>|</c>.
    /// Case-insensitive on read (a hand-edited file is forgiven); writers always emit lowercase.
    /// </summary>
    public static bool IsWireForm(string? value)
    {
        if (value is not { Length: WireLength })
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

    /// <summary>Compares two wire digests — case-insensitively, since reading forgives case.</summary>
    public static bool Matches(string? left, string? right)
        => left is not null && right is not null && left.Equals(right, StringComparison.OrdinalIgnoreCase);

    private static string? IdentityArgs(int argumentCount)
        => argumentCount == 0
            ? null
            : string.Join(ArgsPositionSeparator, Enumerable.Range(1, argumentCount));

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
