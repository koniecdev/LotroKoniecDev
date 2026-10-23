using System.Diagnostics.CodeAnalysis;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;

namespace LotroKoniecDev.TranslationSystem.API.Parsing;

/// <summary>
/// The fields exactly as they appear: nothing unescaped, no number parsed. <see cref="SourceDigest"/> is
/// <see langword="null"/> on a six-column line (ADR-0047 §2).
/// </summary>
internal sealed record CarvedTranslationLine(
    string FileId,
    string GossipId,
    string Content,
    string ArgsOrder,
    string ArgsId,
    string Approved,
    string? SourceDigest);

/// <summary>
/// Cuts a <c>||</c> line into six or seven fields (ADR-0042): the ids scanning forward, the tail
/// scanning backward, and the content is what is left, so it may hold <c>||</c> and end in any number
/// of <c>|</c>.
/// </summary>
/// <remarks>
/// <para>
/// Never <c>string.Split</c>: it takes boundaries greedily, so a pipe at the end of the content merges
/// with the next separator and the boundary lands one character early (#597). Only the content can
/// hold a <c>|</c>, so the separator is the first two pipes of a run going forward and the last two
/// going backward.
/// </para>
/// <para>
/// The patcher has a copy in its own <c>Parsers</c> namespace: the contexts share the file, never the
/// code. Change both in the same commit; the parity tests keep them equal.
/// </para>
/// <para>
/// The last field decides the column count (ADR-0047 §2): 16 hex characters can only be a
/// <c>source_digest</c>, anything else is <c>approved</c>.
/// </para>
/// </remarks>
internal static class TranslationLineCarver
{
    private const string FieldSeparator = "||";

    /// <summary>
    /// <see langword="false"/> when too few separators leave no room for a content field.
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

        // Each backward step searches only up to where the last separator began, so the pipes around
        // an empty args column are never read as the next separator.
        int beforeLastField = line.AsSpan().LastIndexOf(FieldSeparator);
        if (beforeLastField < contentStart)
        {
            return false;
        }

        // Trimmed: readers always trimmed `approved`, and a digest with a stray trailing space must not
        // read as six columns, or the content would swallow `||NULL` and be stored as the source.
        string lastField = line[(beforeLastField + FieldSeparator.Length)..].TrimEnd();

        string? sourceDigest = null;
        int beforeApproved = beforeLastField;
        int approvedEnd = line.Length;

        if (SourceHash.IsWireDigest(lastField))
        {
            sourceDigest = lastField;
            approvedEnd = beforeLastField;

            beforeApproved = line.AsSpan(0, beforeLastField).LastIndexOf(FieldSeparator);
            if (beforeApproved < contentStart)
            {
                return false;
            }
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
            Approved: line[(beforeApproved + FieldSeparator.Length)..approvedEnd],
            SourceDigest: sourceDigest);

        return true;
    }
}
