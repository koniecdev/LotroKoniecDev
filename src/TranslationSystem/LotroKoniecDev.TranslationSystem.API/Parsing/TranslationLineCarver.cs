using System.Diagnostics.CodeAnalysis;

namespace LotroKoniecDev.TranslationSystem.API.Parsing;

/// <summary>
/// One carved <c>||</c> line, fields verbatim — no escape unfolded, no number parsed. Content may
/// legally contain the separator and may end in any number of <c>|</c>.
/// </summary>
internal sealed record CarvedTranslationLine(
    string FileId,
    string GossipId,
    string Content,
    string ArgsOrder,
    string ArgsId,
    string Approved);

/// <summary>
/// Splits a <c>||</c> line into its six fields by anchoring from both ends (ADR-0042): the two id
/// columns are found by scanning forward from the start, the three trailing columns by scanning
/// backward from the end, and content is whatever lies between. Content may therefore contain the
/// separator itself and may end in an odd run of <c>|</c>.
/// </summary>
/// <remarks>
/// <para>
/// Scanning is what makes this exact — <c>string.Split</c> resolves every boundary greedily left to
/// right, so a trailing pipe in the content merges with the separator that follows it and the
/// boundary lands one character early (#597). Nothing but content can contain a <c>|</c>, so at a
/// boundary a run of pipes is always <c>content_pipes + 2</c> and the separator is the run's first
/// two characters going forward, its last two going backward.
/// </para>
/// <para>
/// The patcher owns an identical copy in its own <c>Parsers</c> namespace — the two bounded contexts
/// share the file, never code (CLAUDE.md). The parity suites are what keep the copies honest.
/// </para>
/// </remarks>
internal static class TranslationLineCarver
{
    private const string FieldSeparator = "||";

    /// <summary>
    /// Carves <paramref name="line"/> into its six fields, or answers <see langword="false"/> when
    /// the line does not carry five separators that leave room for a content field.
    /// </summary>
    public static bool TryCarve(string line, [NotNullWhen(true)] out CarvedTranslationLine? carved)
    {
        ArgumentNullException.ThrowIfNull(line);

        carved = null;

        int fileIdEnd = line.IndexOf(FieldSeparator, StringComparison.Ordinal);
        if (fileIdEnd < 0)
        {
            return false;
        }

        int gossipIdStart = fileIdEnd + FieldSeparator.Length;

        int gossipIdEnd = line.IndexOf(FieldSeparator, gossipIdStart, StringComparison.Ordinal);
        if (gossipIdEnd < 0)
        {
            return false;
        }

        int contentStart = gossipIdEnd + FieldSeparator.Length;

        // Each backward step searches the slice that ENDS where the separator it already found
        // begins, so a match always fits entirely inside the remaining region. That is what stops
        // the pair straddling an empty args column from being mistaken for the next separator.
        int beforeApproved = line.AsSpan().LastIndexOf(FieldSeparator);
        if (beforeApproved < contentStart)
        {
            return false;
        }

        int beforeArgsId = line.AsSpan(0, beforeApproved).LastIndexOf(FieldSeparator);
        if (beforeArgsId < contentStart)
        {
            return false;
        }

        int contentEnd = line.AsSpan(0, beforeArgsId).LastIndexOf(FieldSeparator);
        if (contentEnd < contentStart)
        {
            return false;
        }

        carved = new CarvedTranslationLine(
            FileId: line[..fileIdEnd],
            GossipId: line[gossipIdStart..gossipIdEnd],
            Content: line[contentStart..contentEnd],
            ArgsOrder: line[(contentEnd + FieldSeparator.Length)..beforeArgsId],
            ArgsId: line[(beforeArgsId + FieldSeparator.Length)..beforeApproved],
            Approved: line[(beforeApproved + FieldSeparator.Length)..]);

        return true;
    }
}
