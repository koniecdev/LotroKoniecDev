using LotroKoniecDev.Primitives.Constants;

namespace LotroKoniecDev.Domain.Models;

/// <summary>
/// Represents a translation entry for a text fragment.
/// </summary>
public sealed class Translation
{
    public int FileId { get; init; }
    public ulong GossipId { get; init; }

    /// <summary>
    /// The raw fragment text, ready to be written into the DAT — the parser already unfolded the
    /// file's escape sequences (ADR-0039), so this never carries a <c>\n</c> two-character pair.
    /// </summary>
    public string Content { get; init; } = string.Empty;
    public int[]? ArgsOrder { get; init; }
    public int[]? ArgsId { get; init; }
    public bool IsApproved { get; init; } = true;

    /// <summary>
    /// Indicates whether this translation has argument reordering information.
    /// </summary>
    public bool HasArguments => ArgsOrder is { Length: > 0 };

    /// <summary>
    /// Gets the fragment ID — the 8-byte unsigned <see cref="Fragment.FragmentId"/> this row
    /// targets. Equal to <see cref="GossipId"/>, surfaced under the DAT-domain name.
    /// </summary>
    public ulong FragmentId => GossipId;

    /// <summary>
    /// Splits the content into text pieces using the separator.
    /// The separator marks positions where game variables are inserted.
    /// </summary>
    /// <returns>Array of text pieces.</returns>
    public string[] GetPieces() =>
        Content.Split([DatFileConstants.PieceSeparator], StringSplitOptions.None);

    public override string ToString() =>
        $"Translation[File={FileId}, Gossip={GossipId}, Length={Content.Length}]";
}
