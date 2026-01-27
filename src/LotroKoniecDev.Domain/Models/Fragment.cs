using System.Text;
using LotroKoniecDev.Domain.Core.Utilities;

namespace LotroKoniecDev.Domain.Models;

/// <summary>
/// Represents a text fragment within a LOTRO subfile.
/// Contains text pieces, argument references, and argument strings.
/// </summary>
public sealed class Fragment
{

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

        for (int i = 0; i < numPieces; i++)
        {
            int pieceSize = VarLenEncoder.Read(reader);
            byte[] bytes = reader.ReadBytes(pieceSize * 2); // UTF-16LE (2 bytes per char)
            Pieces.Add(Encoding.Unicode.GetString(bytes));
        }
    }

    private void ReadArgRefs(BinaryReader reader)
    {
        int numArgRefs = reader.ReadInt32();

        for (int i = 0; i < numArgRefs; i++)
        {
            ArgRefs.Add(reader.ReadBytes(4));
        }
    }

    private void ReadArgStrings(BinaryReader reader)
    {
        int numArgStringGroups = reader.ReadByte();

        for (int i = 0; i < numArgStringGroups; i++)
        {
            var group = new List<string>();
            int numStrings = reader.ReadInt32();

            for (int j = 0; j < numStrings; j++)
            {
                int strSize = VarLenEncoder.Read(reader);
                byte[] bytes = reader.ReadBytes(strSize * 2);
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
