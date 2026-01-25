namespace LotroKoniecDev.Models;

public class SubFile
{
    public int FileId { get; set; }
    public int Version { get; set; }
    public byte[] Unknown1 { get; private set; } = new byte[4];
    public byte Unknown2 { get; private set; }
    public Dictionary<ulong, Fragment> Fragments { get; } = new();

    public static bool IsTextFile(int fileId) => (fileId >> 24) == 0x25;

    public void Parse(byte[] data)
    {
        using var stream = new MemoryStream(data);
        using var reader = new BinaryReader(stream);

        FileId = reader.ReadInt32();

        if (!IsTextFile(FileId))
        {
            // Nie-tekstowy plik - zachowaj surowe dane
            return;
        }

        Unknown1 = reader.ReadBytes(4);
        Unknown2 = reader.ReadByte();

        int numFragments = ReadVarLen(reader);

        for (int i = 0; i < numFragments; i++)
        {
            var fragment = new Fragment();
            fragment.Parse(reader);
            Fragments[fragment.FragmentId] = fragment;
        }
    }

    public byte[] Serialize(int[]? argsOrder = null, int[]? argsId = null,
                            int? targetFileId = null, ulong? targetFragmentId = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(FileId);
        writer.Write(Unknown1);
        writer.Write(Unknown2);

        WriteVarLen(writer, Fragments.Count);

        foreach (var (fragmentId, fragment) in Fragments)
        {
            // Jesli to docelowy fragment i mamy args do przetasowania
            if (fragmentId == targetFragmentId && argsOrder != null && argsId != null)
            {
                ReorderArgs(fragment, argsOrder, argsId);
            }

            fragment.Write(writer);
        }

        return stream.ToArray();
    }

    private void ReorderArgs(Fragment fragment, int[] argsOrder, int[] argsId)
    {
        // argsOrder jest 0-indexed (juz po konwersji z 1-indexed)
        for (int i = 0; i < argsOrder.Length && i < fragment.ArgRefs.Count; i++)
        {
            int newArgId = argsId[argsOrder[i]];
            fragment.ArgRefs[i] = BitConverter.GetBytes(newArgId);
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
