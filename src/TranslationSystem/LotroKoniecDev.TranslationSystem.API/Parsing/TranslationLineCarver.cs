using System.Diagnostics.CodeAnalysis;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;

namespace LotroKoniecDev.TranslationSystem.API.Parsing;

/// <summary>
/// One <c>||</c> line cut into fields, exactly as they appear: nothing unescaped, no number parsed.
/// The content may hold the separator and may end in any number of <c>|</c>.
/// <see cref="SourceDigest"/> is <see langword="null"/> on a six-column line (ADR-0047 §2).
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
/// Cuts a <c>||</c> line into its six or seven fields by working from both ends (ADR-0042). The two
/// id columns are found by scanning forward from the start, the last columns by scanning backward
/// from the end, and the content is whatever is left in between. So the content may hold the
/// separator itself and may end in an odd number of <c>|</c>.
/// </summary>
/// <remarks>
/// <para>
/// Scanning is what makes this exact. <c>string.Split</c> takes every boundary greedily from left to
/// right, so a pipe at the end of the content merges with the separator after it and the boundary
/// lands one character too early (#597). Only the content can hold a <c>|</c>, so at a boundary a run
/// of pipes is always <c>content_pipes + 2</c>: the separator is the first two characters of the run
/// going forward, and the last two going backward.
/// </para>
/// <para>
/// The patcher has an identical copy in its own <c>Parsers</c> namespace, because the two bounded
/// contexts share the file and never the code (CLAUDE.md). The parity test suites keep the copies the
/// same.
/// </para>
/// <para>
/// How many columns there are at the end is decided by <b>looking at the last field</b> (ADR-0047 §2):
/// 16 hex characters can only be a <c>source_digest</c>, and anything else can only be
/// <c>approved</c>. There is no other reading, and that is what lets six-column files, whether older
/// exports, hand-made ones or the existing fixtures, be cut exactly as before.
/// </para>
/// </remarks>
internal static class TranslationLineCarver
{
    private const string FieldSeparator = "||";

    /// <summary>
    /// Cuts <paramref name="line"/> into its six or seven fields. It returns <see langword="false"/>
    /// when the line has too few separators outside the content to leave room for a content field.
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

        // Each backward step searches only the part of the line that ends where the separator it just
        // found begins, so a match always fits inside what is left. That is what stops the pair of
        // pipes around an empty args column from being read as the next separator.
        int beforeLastField = line.AsSpan().LastIndexOf(FieldSeparator);
        if (beforeLastField < contentStart)
        {
            return false;
        }

        // We look at the last field with trailing spaces removed. `approved` has always allowed them,
        // because the readers trim it, and a digest followed by a stray space or tab must not make the
        // line read as six columns. In that reading the content would swallow `||NULL` and the
        // approved column would be the digest, and the TMS import would quietly store that as the
        // row's source.
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
