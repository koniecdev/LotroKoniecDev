using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LotroKoniecDev.Primitives.Constants;

namespace LotroKoniecDev.Domain.Models;

/// <summary>
/// The patcher's half of the <c>source_digest</c> contract (ADR-0047): 16 lower-case hex characters
/// that identify the export-form triple <c>(text, args_order, args_id)</c> a fragment holds.
/// The TMS computes the same value from its stored <c>TranslationSource</c>, where it is called
/// <c>SourceHash</c>, and ships it as the seventh column of the translation file. The patcher
/// recomputes it from the loaded fragment to decide whether a row may overwrite what the DAT holds
/// right now.
/// </summary>
/// <remarks>
/// <para>
/// <b>Framing.</b> SHA-256 over the three fields, each written as <c>marker | UTF-16 code-unit count
/// (little-endian int32) | UTF-16LE bytes</c>. A missing field is the single marker byte <c>0</c>, a
/// present one is <c>1</c> followed by its header and bytes. This framing is what keeps <c>null</c>
/// apart from the empty string, and <c>("ab","c")</c> apart from <c>("a","bc")</c>.
/// The value on the wire is the first eight <b>bytes</b> of the digest, in digest order, as lower-case
/// hex. It is never a <c>ulong</c> printed with <c>x16</c>, which would reverse the byte order.
/// </para>
/// <para>
/// <b>Two copies on purpose.</b> The two bounded contexts share the file and never the code
/// (CLAUDE.md), so this is written separately from the TMS' <c>SourceHash</c>. A golden fixture
/// pinned by a unit test in both contexts is what keeps the copies the same (ADR-0047 §6). If they
/// drift, a build fails instead of an update day.
/// </para>
/// </remarks>
public static class SourceDigest
{
    /// <summary>Length in characters of the wire form: the first 8 digest bytes as hex.</summary>
    public const int WireLength = 16;

    private const byte NullFieldMarker = 0;
    private const byte PresentFieldMarker = 1;
    private const int FieldHeaderSize = 5;
    private const int WireBytes = WireLength / 2;
    private const char ArgsPositionSeparator = '-';

    /// <summary>
    /// The digest of what <paramref name="fragment"/> holds right now, built exactly the way
    /// <c>export</c> writes it: the pieces joined with the placeholder, and args columns counted up
    /// from the fragment's own number of argument references.
    /// </summary>
    public static string ForFragment(Fragment fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);

        return ForExportForm(
            string.Join(DatFileConstants.PieceSeparator, fragment.Pieces),
            fragment.ArgRefs.Count);
    }

    /// <summary>
    /// The digest of <paramref name="text"/> as the exporter would write it for a fragment with
    /// <paramref name="argumentCount"/> argument references: the args columns are <c>1-2-…-n</c>, or
    /// absent when the fragment has none. It is used for the fragment's current content, and with a
    /// row's translated text for the digest that row would leave behind once written.
    /// </summary>
    public static string ForExportForm(string text, int argumentCount)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegative(argumentCount);

        string? identityArgs = IdentityArgs(argumentCount);

        return Compute(text, identityArgs, identityArgs);
    }

    /// <summary>
    /// Hashes the triple in its value-object form. An absent args column is <see langword="null"/>
    /// and never the literal text <c>NULL</c> the file uses, or the two contexts would not agree.
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
    /// Whether <paramref name="value"/> can be a wire digest: exactly 16 hex characters. This is how
    /// both carvers tell a seventh column from a six-column line's <c>approved</c> field (ADR-0047
    /// §2). Nothing else can sit in that slot, and hex never holds a <c>|</c>. Reading accepts upper
    /// case as well, so a hand-edited file still works; writers always emit lower case.
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

    /// <summary>Compares two wire digests without case, because reading accepts either case.</summary>
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
