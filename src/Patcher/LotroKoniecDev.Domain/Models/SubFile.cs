using LotroKoniecDev.Domain.Core.Utilities;
using LotroKoniecDev.Primitives.Constants;

namespace LotroKoniecDev.Domain.Models;

/// <summary>
/// One subfile of a LOTRO DAT archive: its metadata, and its text fragments when it holds text.
/// </summary>
public sealed class SubFile
{
    private const int MinEncodedFragmentSize = 17; // FragmentId (8) + piece count (4) + arg-ref count (4) + arg-string-group count (1)

    private byte[] _rawData = [];

    public int FileId { get; private set; }
    public int Version { get; set; }
    public byte[] Unknown1 { get; private set; } = new byte[4];
    public byte Unknown2 { get; private set; }

    /// <summary>
    /// The fragments in the order the subfile stores them. That order is often not sorted by id, and
    /// <see cref="Serialize"/> must write it back unchanged (#1194).
    /// </summary>
    public OrderedDictionary<ulong, Fragment> Fragments { get; } = new();

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
            // We do not parse a non-text subfile, but Serialize must still give back the bytes it
            // was read from (#1194).
            _rawData = (byte[])data.Clone();
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

    /// <remarks>
    /// The result matches the bytes <see cref="Parse"/> read only when the parser understood all of
    /// them. A repeated fragment id, bytes after the last fragment, a length stored in the long form or
    /// broken UTF-16 come back changed. So a caller that writes the result into the DAT checks the
    /// unchanged subfile first, as <c>LoadSubFile</c> does for the patcher (#1194).
    /// </remarks>
    /// <param name="argsOrder">The new argument order, 0-indexed. Leave null to keep the current one.</param>
    /// <param name="argsId">The new argument ids. Leave null to keep the current ones.</param>
    /// <param name="targetFragmentId">The fragment the reordering applies to.</param>
    public byte[] Serialize(int[]? argsOrder = null, int[]? argsId = null, ulong? targetFragmentId = null)
    {
        if (!IsText)
        {
            return (byte[])_rawData.Clone();
        }

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
