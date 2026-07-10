using System.Text;
using LotroKoniecDev.Domain.Core.Utilities;

namespace LotroKoniecDev.Domain.Models;

/// <summary>
/// Represents a text fragment within a LOTRO subfile.
/// Contains text pieces, argument references, and argument strings.
/// </summary>
public sealed class Fragment
{
    private const int BytesPerUtf16Char = 2;
    private const int ArgRefSize = 4;
    private const int MinEncodedPieceSize = 1; // a zero-length piece still carries its VarLen length byte
    private const int MinEncodedArgStringSize = 1; // a zero-length argument string still carries its VarLen length byte
    private const int MinEncodedArgStringGroupSize = 4; // an empty group still carries its string-count int

    public ulong FragmentId { get; private set; }
    public List<string> Pieces { get; set; } = [];
    public List<byte[]> ArgRefs { get; private set; } = [];
    public List<List<string>> ArgStrings { get; private set; } = [];

    /// <summary>
    /// Indicates whether this fragment has argument references.
    /// </summary>
    public bool HasArguments => ArgRefs.Count > 0;

    /// <summary>
    /// Gets the combined text content of all pieces.
    /// </summary>
    public string GetFullText(string separator = "") =>
        string.Join(separator, Pieces);

    /// <summary>
    /// Reorders argument references according to the specified order.
    /// Each element in <paramref name="order"/> is a 0-indexed source position.
    /// </summary>
    /// <param name="order">The reordering map (e.g. [1, 0] swaps two args).</param>
    /// <returns>True if reordering succeeded; false if order is invalid.</returns>
    public bool TryReorderArgRefs(int[] order)
    {
        ArgumentNullException.ThrowIfNull(order);

        if (order.Length != ArgRefs.Count)
        {
            return false;
        }

        List<byte[]> reordered = new(order.Length);

        foreach (int sourceIndex in order)
        {
            if (sourceIndex < 0 || sourceIndex >= ArgRefs.Count)
            {
                return false;
            }

            reordered.Add(ArgRefs[sourceIndex]);
        }

        ArgRefs = reordered;
        return true;
    }

    /// <summary>
    /// Parses a fragment from binary data.
    /// </summary>
    /// <param name="reader">The binary reader positioned at the fragment start.</param>
    public void Parse(BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        FragmentId = reader.ReadUInt64();
        ReadPieces(reader);
        ReadArgRefs(reader);
        ReadArgStrings(reader);
    }

    /// <summary>
    /// Writes the fragment to binary format.
    /// </summary>
    /// <param name="writer">The binary writer to write to.</param>
    public void Write(BinaryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.Write(FragmentId);
        WritePieces(writer);
        WriteArgRefs(writer);
        WriteArgStrings(writer);
    }

    private void ReadPieces(BinaryReader reader)
    {
        int numPieces = reader.ReadInt32();
        BinaryBoundsGuard.EnsureCountFits(reader, numPieces, MinEncodedPieceSize, "piece");

        for (int i = 0; i < numPieces; i++)
        {
            int pieceSize = VarLenEncoder.Read(reader);
            BinaryBoundsGuard.EnsureCountFits(reader, pieceSize, BytesPerUtf16Char, "piece character");
            byte[] bytes = reader.ReadBytes(pieceSize * BytesPerUtf16Char);
            Pieces.Add(Encoding.Unicode.GetString(bytes));
        }
    }

    private void ReadArgRefs(BinaryReader reader)
    {
        int numArgRefs = reader.ReadInt32();
        BinaryBoundsGuard.EnsureCountFits(reader, numArgRefs, ArgRefSize, "argument reference");

        for (int i = 0; i < numArgRefs; i++)
        {
            ArgRefs.Add(reader.ReadBytes(ArgRefSize));
        }
    }

    private void ReadArgStrings(BinaryReader reader)
    {
        int numArgStringGroups = reader.ReadByte();
        BinaryBoundsGuard.EnsureCountFits(reader, numArgStringGroups, MinEncodedArgStringGroupSize, "argument string group");

        for (int i = 0; i < numArgStringGroups; i++)
        {
            List<string> group = new List<string>();
            int numStrings = reader.ReadInt32();
            BinaryBoundsGuard.EnsureCountFits(reader, numStrings, MinEncodedArgStringSize, "argument string");

            for (int j = 0; j < numStrings; j++)
            {
                int strSize = VarLenEncoder.Read(reader);
                BinaryBoundsGuard.EnsureCountFits(reader, strSize, BytesPerUtf16Char, "argument string character");
                byte[] bytes = reader.ReadBytes(strSize * BytesPerUtf16Char);
                group.Add(Encoding.Unicode.GetString(bytes));
            }

            ArgStrings.Add(group);
        }
    }

    private void WritePieces(BinaryWriter writer)
    {
        writer.Write(Pieces.Count);

        foreach (string piece in Pieces)
        {
            VarLenEncoder.Write(writer, piece.Length);
            writer.Write(Encoding.Unicode.GetBytes(piece));
        }
    }

    private void WriteArgRefs(BinaryWriter writer)
    {
        writer.Write(ArgRefs.Count);

        foreach (byte[] argRef in ArgRefs)
        {
            writer.Write(argRef);
        }
    }

    private void WriteArgStrings(BinaryWriter writer)
    {
        writer.Write((byte)ArgStrings.Count);

        foreach (List<string> group in ArgStrings)
        {
            writer.Write(group.Count);

            foreach (string str in group)
            {
                VarLenEncoder.Write(writer, str.Length);
                writer.Write(Encoding.Unicode.GetBytes(str));
            }
        }
    }

}
