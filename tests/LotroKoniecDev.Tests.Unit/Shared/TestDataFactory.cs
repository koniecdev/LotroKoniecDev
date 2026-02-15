using System.Text;

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
    /// Creates binary SubFile data with multiple fragments, IDs starting from <paramref name="fragmentId"/>.
    /// Each fragment contains a single "Test" piece.
    /// </summary>
    internal static byte[] CreateTextSubFileData(int fileId, ulong fragmentId, int fragmentCount)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        writer.Write(fileId);
        writer.Write(new byte[4]); // Unknown1
        writer.Write((byte)0); // Unknown2
        writer.Write((byte)fragmentCount); // numFragments (varlen)

        for (int i = 0; i < fragmentCount; i++)
        {
            writer.Write(fragmentId + (ulong)i); // fragmentId
            writer.Write(1); // numPieces
            writer.Write((byte)4); // piece length (varlen)
            writer.Write(Encoding.Unicode.GetBytes("Test"));
            writer.Write(0); // numArgRefs
            writer.Write((byte)0); // numArgStringGroups
        }

        return stream.ToArray();
    }
}
