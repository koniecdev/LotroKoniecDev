using System.Text;

namespace LotroKoniecDev.Models;

public class Fragment
{
    public ulong FragmentId { get; set; }
    public List<string> Pieces { get; set; } = new();
    public List<byte[]> ArgRefs { get; set; } = new();
    public List<List<string>> ArgStrings { get; set; } = new();

    public void Parse(BinaryReader reader)
    {
        FragmentId = reader.ReadUInt64();

        int numPieces = reader.ReadInt32();
        for (int i = 0; i < numPieces; i++)
        {
            int pieceSize = ReadVarLen(reader);
            byte[] bytes = reader.ReadBytes(pieceSize * 2); // UTF-16LE
            Pieces.Add(Encoding.Unicode.GetString(bytes));
        }

        int numArgRefs = reader.ReadInt32();
        for (int i = 0; i < numArgRefs; i++)
        {
            ArgRefs.Add(reader.ReadBytes(4));
        }

        int numArgStringGroups = reader.ReadByte();
        for (int i = 0; i < numArgStringGroups; i++)
        {
            var group = new List<string>();
            int numStrings = reader.ReadInt32();
            for (int j = 0; j < numStrings; j++)
            {
                int strSize = ReadVarLen(reader);
                byte[] bytes = reader.ReadBytes(strSize * 2);
                group.Add(Encoding.Unicode.GetString(bytes));
            }
            ArgStrings.Add(group);
        }
    }

    public void Write(BinaryWriter writer)
    {
        writer.Write(FragmentId);
        writer.Write(Pieces.Count);

        foreach (var piece in Pieces)
        {
            WriteVarLen(writer, piece.Length);
            writer.Write(Encoding.Unicode.GetBytes(piece));
        }

        writer.Write(ArgRefs.Count);
        foreach (var argRef in ArgRefs)
        {
            writer.Write(argRef);
        }

        writer.Write((byte)ArgStrings.Count);
        foreach (var group in ArgStrings)
        {
            writer.Write(group.Count);
            foreach (var str in group)
            {
                WriteVarLen(writer, str.Length);
                writer.Write(Encoding.Unicode.GetBytes(str));
            }
        }
    }

    private static int ReadVarLen(BinaryReader reader)
    {
        int value = reader.ReadByte();
        if ((value & 0x80) != 0)
        {
            value = ((value ^ 0x80) << 8) | reader.ReadByte();
        }
        return value;
    }

    private static void WriteVarLen(BinaryWriter writer, int value)
    {
        if (value >= 0x80)
        {
            writer.Write((byte)((value >> 8) ^ 0x80));
            writer.Write((byte)(value & 0xFF));
        }
        else
        {
            writer.Write((byte)value);
        }
    }
}
