using System.Text;
using LotroKoniecDev.Domain.Core.Utilities;
using LotroKoniecDev.Primitives.Constants;

namespace LotroKoniecDev.Domain.Models;

/// <summary>
/// One text fragment inside a LOTRO subfile: its text pieces, its argument references and its
/// argument strings.
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

    public bool HasArguments => ArgRefs.Count > 0;

    /// <summary>Joins the pieces back into one string, with <paramref name="separator"/> between them.</summary>
    public string GetFullText(string separator = "") =>
        string.Join(separator, Pieces);

    /// <param name="order">
    /// Where each argument comes from, as 0-indexed source positions. For example [1, 0] swaps two
    /// arguments.
    /// </param>
    /// <returns>False when the order does not fit this fragment's arguments.</returns>
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

    /// <param name="reader">A reader placed at the start of the fragment.</param>
    public void Parse(BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        FragmentId = reader.ReadUInt64();
        ReadPieces(reader);
        ReadArgRefs(reader);
        ReadArgStrings(reader);
    }

    /// <summary>
    /// Whether a text piece fits into the DAT. This is what <see cref="Write"/> requires, and it is
    /// public so callers can check new content up front instead of hitting an exception halfway
    /// through a subfile (#598). A piece read out of the DAT always passes; only translated text can
    /// fail.
    /// </summary>
    /// <param name="piece">The piece to check.</param>
    public static bool IsWritablePiece(string piece)
    {
        ArgumentNullException.ThrowIfNull(piece);

        return piece.Length <= DatFileConstants.MaxTextPieceLength;
    }

    /// <exception cref="ArgumentOutOfRangeException">
    /// A piece failed <see cref="IsWritablePiece"/>. This throws on purpose: the length prefix cannot
    /// hold that number, and writing a shortened one would corrupt the whole subfile.
    /// </exception>
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
