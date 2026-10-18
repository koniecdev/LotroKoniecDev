using System.Text;
using LotroKoniecDev.Domain.Core.Utilities;

namespace LotroKoniecDev.Tests.Unit.Shared;

internal static class TestDataFactory
{
    /// <summary>
    /// Creates binary SubFile data matching the DAT format:
    /// FileId (4B) | Unknown1 (4B) | Unknown2 (1B) | FragCount (VarLen) | Fragment[]
    /// Each fragment: FragmentId (8B) | PieceCount (int) | Piece (VarLen + UTF-16LE) | ArgRefCount (int) | ArgStringGroupCount (byte)
    /// </summary>
    internal static byte[] CreateTextSubFileData(int fileId, string text)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        writer.Write(fileId);
        writer.Write(new byte[4]); // Unknown1
        writer.Write((byte)0); // Unknown2
        writer.Write((byte)1); // numFragments (varlen)

        // Fragment
        writer.Write((ulong)1); // fragmentId
        writer.Write(1); // numPieces
        writer.Write((byte)text.Length); // piece length (varlen)
        writer.Write(Encoding.Unicode.GetBytes(text));
        writer.Write(0); // numArgRefs
        writer.Write((byte)0); // numArgStringGroups

        return stream.ToArray();
    }

    /// <summary>
    /// Creates binary SubFile data with a single fragment containing argument references.
    /// </summary>
    internal static byte[] CreateTextSubFileDataWithArgs(
        int fileId,
        ulong fragmentId,
        string[] pieces,
        byte[][] argRefs)
        => CreateTextSubFileDataWithArgStrings(fileId, new byte[4], 0, fragmentId, pieces, argRefs, []);

    /// <summary>
    /// Creates binary SubFile data with the given header bytes and a single fragment that carries
    /// argument references and argument string groups.
    /// </summary>
    internal static byte[] CreateTextSubFileDataWithArgStrings(
        int fileId,
        byte[] unknown1,
        byte unknown2,
        ulong fragmentId,
        string[] pieces,
        byte[][] argRefs,
        string[][] argStringGroups)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        writer.Write(fileId);
        writer.Write(unknown1);
        writer.Write(unknown2);
        VarLenEncoder.Write(writer, 1); // numFragments

        writer.Write(fragmentId);
        writer.Write(pieces.Length);

        foreach (string piece in pieces)
        {
            VarLenEncoder.Write(writer, piece.Length);
            writer.Write(Encoding.Unicode.GetBytes(piece));
        }

        writer.Write(argRefs.Length);

        foreach (byte[] argRef in argRefs)
        {
            writer.Write(argRef);
        }

        writer.Write((byte)argStringGroups.Length);

        foreach (string[] group in argStringGroups)
        {
            writer.Write(group.Length);

            foreach (string argString in group)
            {
                VarLenEncoder.Write(writer, argString.Length);
                writer.Write(Encoding.Unicode.GetBytes(argString));
            }
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Creates binary SubFile data with one "Test" fragment per id, in the given order. No ids gives a
    /// valid subfile with no fragments.
    /// </summary>
    internal static byte[] CreateTextSubFileDataWithFragmentIds(int fileId, params ulong[] fragmentIds)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        writer.Write(fileId);
        writer.Write(new byte[4]); // Unknown1
        writer.Write((byte)0); // Unknown2
        VarLenEncoder.Write(writer, fragmentIds.Length);

        foreach (ulong fragmentId in fragmentIds)
        {
            writer.Write(fragmentId);
            writer.Write(1); // numPieces
            writer.Write((byte)4); // piece length (varlen)
            writer.Write(Encoding.Unicode.GetBytes("Test"));
            writer.Write(0); // numArgRefs
            writer.Write((byte)0); // numArgStringGroups
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Builds binary SubFile data that says it has <paramref name="declaredFragmentCount"/> fragments but
    /// contains none. The count is impossible, which is what exercises the parser's bounds checks.
    /// </summary>
    internal static byte[] CreateTextSubFileDataWithImpossibleFragmentCount(int fileId, int declaredFragmentCount)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        writer.Write(fileId);
        writer.Write(new byte[4]); // Unknown1
        writer.Write((byte)0); // Unknown2
        VarLenEncoder.Write(writer, declaredFragmentCount); // numFragments (varlen)

        return stream.ToArray();
    }

    /// <summary>
    /// Creates binary SubFile data with multiple fragments, IDs starting from <paramref name="fragmentId"/>.
    /// Each fragment contains a single "Test" piece.
    /// </summary>
    internal static byte[] CreateTextSubFileData(int fileId, ulong fragmentId, int fragmentCount)
        => CreateTextSubFileDataWithFragmentIds(
            fileId,
            [.. Enumerable.Range(0, fragmentCount).Select(i => fragmentId + (ulong)i)]);

    /// <summary>
    /// Creates binary SubFile data with one "Test" fragment per id, stored in exactly the given order.
    /// The same id may be given twice.
    /// </summary>
    internal static byte[] CreateTextSubFileDataInFileOrder(int fileId, params ulong[] fragmentIds) =>
        CreateTextSubFileDataWithTrailingBytes(fileId, fragmentIds.Length, fragmentIds, []);

    /// <summary>
    /// Creates binary SubFile data that declares <paramref name="declaredFragmentCount"/> fragments, stores
    /// one "Test" fragment per id in <paramref name="storedFragmentIds"/>, and ends with
    /// <paramref name="trailingBytes"/>. Storing more fragments than declared puts whole fragments after
    /// the last declared one.
    /// </summary>
    internal static byte[] CreateTextSubFileDataWithTrailingBytes(
        int fileId,
        int declaredFragmentCount,
        ulong[] storedFragmentIds,
        byte[] trailingBytes)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        writer.Write(fileId);
        writer.Write(new byte[4]); // Unknown1
        writer.Write((byte)0); // Unknown2
        VarLenEncoder.Write(writer, declaredFragmentCount);

        foreach (ulong fragmentId in storedFragmentIds)
        {
            WriteTestFragment(writer, fragmentId);
        }

        writer.Write(trailingBytes);

        return stream.ToArray();
    }

    /// <summary>
    /// Creates binary SubFile data with <paramref name="fragmentCount"/> "Test" fragments, whose count is
    /// written in the two-byte VarLen form even when it fits in one byte. VarLenEncoder.Write never
    /// writes that form for a value below 128, so the bytes are written here by hand.
    /// </summary>
    internal static byte[] CreateTextSubFileDataWithTwoByteFragmentCount(int fileId, int fragmentCount)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        writer.Write(fileId);
        writer.Write(new byte[4]); // Unknown1
        writer.Write((byte)0); // Unknown2
        writer.Write((byte)(0x80 | (fragmentCount >> 8)));
        writer.Write((byte)(fragmentCount & 0xFF));

        for (int i = 0; i < fragmentCount; i++)
        {
            WriteTestFragment(writer, (ulong)i + 1);
        }

        return stream.ToArray();
    }

    /// <summary>Creates binary SubFile data made of <paramref name="fileId"/> followed by <paramref name="body"/>.</summary>
    internal static byte[] CreateNonTextSubFileData(int fileId, byte[] body)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        writer.Write(fileId);
        writer.Write(body);

        return stream.ToArray();
    }

    /// <summary>
    /// Creates binary SubFile data with one fragment whose piece is made of the given UTF-16 code units.
    /// They are written one by one, because Encoding.Unicode would replace an unpaired surrogate with
    /// U+FFFD.
    /// </summary>
    internal static byte[] CreateTextSubFileDataWithRawPiece(int fileId, ushort[] pieceCodeUnits)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        WriteOneFragmentHeader(writer, fileId);
        writer.Write(1UL); // fragmentId
        writer.Write(1); // numPieces
        WriteRawCodeUnits(writer, pieceCodeUnits);
        writer.Write(0); // numArgRefs
        writer.Write((byte)0); // numArgStringGroups

        return stream.ToArray();
    }

    /// <summary>
    /// Creates binary SubFile data with one fragment that has a "Test" piece and one argument string made
    /// of the given UTF-16 code units. They are written one by one, because Encoding.Unicode would replace
    /// an unpaired surrogate with U+FFFD.
    /// </summary>
    internal static byte[] CreateTextSubFileDataWithRawArgString(int fileId, ushort[] argStringCodeUnits)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        WriteOneFragmentHeader(writer, fileId);
        writer.Write(1UL); // fragmentId
        writer.Write(1); // numPieces
        VarLenEncoder.Write(writer, "Test".Length);
        writer.Write(Encoding.Unicode.GetBytes("Test"));
        writer.Write(0); // numArgRefs
        writer.Write((byte)1); // numArgStringGroups
        writer.Write(1); // numStrings in the group
        WriteRawCodeUnits(writer, argStringCodeUnits);

        return stream.ToArray();
    }

    private static void WriteTestFragment(BinaryWriter writer, ulong fragmentId)
    {
        writer.Write(fragmentId);
        writer.Write(1); // numPieces
        VarLenEncoder.Write(writer, "Test".Length);
        writer.Write(Encoding.Unicode.GetBytes("Test"));
        writer.Write(0); // numArgRefs
        writer.Write((byte)0); // numArgStringGroups
    }

    private static void WriteOneFragmentHeader(BinaryWriter writer, int fileId)
    {
        writer.Write(fileId);
        writer.Write(new byte[4]); // Unknown1
        writer.Write((byte)0); // Unknown2
        VarLenEncoder.Write(writer, 1); // numFragments
    }

    private static void WriteRawCodeUnits(BinaryWriter writer, ushort[] codeUnits)
    {
        VarLenEncoder.Write(writer, codeUnits.Length);

        foreach (ushort codeUnit in codeUnits)
        {
            writer.Write(codeUnit);
        }
    }
}
