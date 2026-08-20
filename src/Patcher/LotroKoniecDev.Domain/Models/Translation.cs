using LotroKoniecDev.Primitives.Constants;

namespace LotroKoniecDev.Domain.Models;

/// <summary>
/// One row of the translation file: the Polish for a single text fragment.
/// </summary>
public sealed class Translation
{
    public int FileId { get; init; }
    public ulong GossipId { get; init; }

    /// <summary>
    /// The raw fragment text, ready to go into the DAT. The parser has already turned the file's
    /// escape sequences back into real characters (ADR-0039), so this never holds a two-character
    /// <c>\n</c>.
    /// </summary>
    public string Content { get; init; } = string.Empty;
    public int[]? ArgsOrder { get; init; }
    public int[]? ArgsId { get; init; }
    public bool IsApproved { get; init; } = true;

    /// <summary>
    /// The <c>source_digest</c> column (ADR-0047): 16 hex characters naming the English this
    /// translation was written against, or <see langword="null"/> on a six-column line. The parser
    /// never rejects a row for missing it, because a file that is six columns throughout must still
    /// parse and still let the game start. It is the patcher's write guard that refuses such a row.
    /// </summary>
    public string? SourceDigest { get; init; }

    public bool HasArguments => ArgsOrder is { Length: > 0 };

    /// <summary>
    /// The 8-byte unsigned <see cref="Fragment.FragmentId"/> this row targets. It is the same value
    /// as <see cref="GossipId"/>, under the name the DAT uses.
    /// </summary>
    public ulong FragmentId => GossipId;

    /// <summary>
    /// Splits the content at the separator that marks where the game inserts its own variables.
    /// </summary>
    public string[] GetPieces() =>
        Content.Split([DatFileConstants.PieceSeparator], StringSplitOptions.None);

    public override string ToString() =>
        $"Translation[File={FileId}, Gossip={GossipId}, Length={Content.Length}]";
}
