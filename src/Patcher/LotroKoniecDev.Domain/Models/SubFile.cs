using LotroKoniecDev.Domain.Core.Utilities;
using LotroKoniecDev.Primitives.Constants;

namespace LotroKoniecDev.Domain.Models;

/// <summary>
/// One subfile of a LOTRO DAT archive: its metadata, and its text fragments when it holds text.
/// </summary>
public sealed class SubFile
{
    private const int MinEncodedFragmentSize = 17; // FragmentId (8) + piece count (4) + arg-ref count (4) + arg-string-group count (1)

    public int FileId { get; private set; }
    public int Version { get; set; }
    public byte[] Unknown1 { get; private set; } = new byte[4];
    public byte Unknown2 { get; private set; }
    public Dictionary<ulong, Fragment> Fragments { get; } = new();

    /// <summary>A text file has 0x25 as the high byte of its file id.</summary>
    public static bool IsTextFile(int fileId) => fileId >> 24 == DatFileConstants.TextFileMarker;

    public bool IsText => IsTextFile(FileId);

    public int FragmentCount => Fragments.Count;

    public void Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        using MemoryStream stream = new(data);
        using BinaryReader reader = new(stream);

        FileId = reader.ReadInt32();

        if (!IsTextFile(FileId))
        {
            // Not a text file: keep the raw bytes and do not parse them.
            return;
        }

        Unknown1 = reader.ReadBytes(4);
        Unknown2 = reader.ReadByte();

        int numFragments = VarLenEncoder.Read(reader);
        BinaryBoundsGuard.EnsureCountFits(reader, numFragments, MinEncodedFragmentSize, "fragment");

        for (int i = 0; i < numFragments; i++)
        {
            Fragment fragment = new();
            fragment.Parse(reader);
            Fragments[fragment.FragmentId] = fragment;
        }
    }

    /// <param name="argsOrder">The new argument order, 0-indexed. Leave null to keep the current one.</param>
    /// <param name="argsId">The new argument ids. Leave null to keep the current ones.</param>
    /// <param name="targetFragmentId">The fragment the reordering applies to.</param>
    public byte[] Serialize(int[]? argsOrder = null, int[]? argsId = null, ulong? targetFragmentId = null)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        writer.Write(FileId);
        writer.Write(Unknown1);
        writer.Write(Unknown2);

        VarLenEncoder.Write(writer, Fragments.Count);

        foreach ((ulong fragmentId, Fragment fragment) in Fragments)
        {
            if (fragmentId == targetFragmentId && argsOrder is not null && argsId is not null)
            {
                ReorderArguments(fragment, argsOrder, argsId);
            }

            fragment.Write(writer);
        }

        return stream.ToArray();
    }

    public bool TryGetFragment(ulong fragmentId, out Fragment? fragment) =>
        Fragments.TryGetValue(fragmentId, out fragment);

    private static void ReorderArguments(Fragment fragment, int[] argsOrder, int[] argsId)
    {
        for (int i = 0; i < argsOrder.Length && i < fragment.ArgRefs.Count; i++)
        {
            int newArgId = argsId[argsOrder[i]];
            fragment.ArgRefs[i] = BitConverter.GetBytes(newArgId);
        }
    }

}
