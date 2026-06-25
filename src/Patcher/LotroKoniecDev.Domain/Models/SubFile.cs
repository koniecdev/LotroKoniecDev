using LotroKoniecDev.Domain.Core.Utilities;
using LotroKoniecDev.Primitives.Constants;

namespace LotroKoniecDev.Domain.Models;

/// <summary>
/// Represents a subfile within a LOTRO DAT archive.
/// Contains metadata and text fragments for text files.
/// </summary>
public sealed class SubFile
{
    public int FileId { get; private set; }
    public int Version { get; set; }
    public byte[] Unknown1 { get; private set; } = new byte[4];
    public byte Unknown2 { get; private set; }
    public Dictionary<ulong, Fragment> Fragments { get; } = new();

    /// <summary>
    /// Determines if a file ID represents a text file.
    /// Text files have 0x25 as the high byte.
    /// </summary>
    public static bool IsTextFile(int fileId) => fileId >> 24 == DatFileConstants.TextFileMarker;

    /// <summary>
    /// Indicates whether this subfile contains text data.
    /// </summary>
    public bool IsText => IsTextFile(FileId);

    /// <summary>
    /// Gets the total number of fragments in this subfile.
    /// </summary>
    public int FragmentCount => Fragments.Count;

    /// <summary>
    /// Parses a subfile from raw binary data.
    /// </summary>
    /// <param name="data">The raw binary data of the subfile.</param>
    public void Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        using MemoryStream stream = new(data);
        using BinaryReader reader = new(stream);

        FileId = reader.ReadInt32();

        if (!IsTextFile(FileId))
        {
            // Non-text file - preserve raw data without parsing
            return;
        }

        Unknown1 = reader.ReadBytes(4);
        Unknown2 = reader.ReadByte();

        int numFragments = VarLenEncoder.Read(reader);

        for (int i = 0; i < numFragments; i++)
        {
            Fragment fragment = new();
            fragment.Parse(reader);
            Fragments[fragment.FragmentId] = fragment;
        }
    }

    /// <summary>
    /// Serializes the subfile to binary format.
    /// </summary>
    /// <param name="argsOrder">Optional argument order for reordering (0-indexed).</param>
    /// <param name="argsId">Optional argument IDs for reordering.</param>
    /// <param name="targetFragmentId">The fragment ID to apply argument reordering to.</param>
    /// <returns>The serialized binary data.</returns>
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

    /// <summary>
    /// Tries to get a fragment by its ID.
    /// </summary>
    /// <param name="fragmentId">The fragment ID to find.</param>
    /// <param name="fragment">The found fragment, or null if not found.</param>
    /// <returns>True if the fragment was found; otherwise, false.</returns>
    public bool TryGetFragment(ulong fragmentId, out Fragment? fragment) =>
        Fragments.TryGetValue(fragmentId, out fragment);

    /// <summary>
    /// Reorders arguments in a fragment based on specified order.
    /// </summary>
    private static void ReorderArguments(Fragment fragment, int[] argsOrder, int[] argsId)
    {
        for (int i = 0; i < argsOrder.Length && i < fragment.ArgRefs.Count; i++)
        {
            int newArgId = argsId[argsOrder[i]];
            fragment.ArgRefs[i] = BitConverter.GetBytes(newArgId);
        }
    }

}
