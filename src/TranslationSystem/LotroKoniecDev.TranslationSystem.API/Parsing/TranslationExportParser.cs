using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;

namespace LotroKoniecDev.TranslationSystem.API.Parsing;

/// <summary>
/// Reads an uploaded <c>exported.txt</c> in the LOTRO <c>||</c> format
/// (<c>file_id||gossip_id||content||args_order||args_id||approved||source_digest</c>). It cuts the
/// fields out by working from both ends of the line (ADR-0042) and unescapes the content (ADR-0039),
/// so the catalog stores the raw source text and not its file form.
/// The TMS has its own parser, and golden fixtures plus round-trip tests keep it in step with the
/// patcher's.
/// </summary>
/// <remarks>
/// The last column, <c>source_digest</c> (ADR-0047), is optional. A six-column upload, an older export
/// or a hand-made file, imports exactly as before.
/// When the column is there it is checked against the row itself, so a wrong file, or a difference
/// between the two contexts' digest code, is rejected per row here (ADR-0042) instead of producing an
/// artifact every player's patcher would then refuse.
/// </remarks>
internal sealed class TranslationExportParser : ITranslationExportParser
{
    private const string FieldSeparator = "||";
    private const int SeparatorCount = 5;
    private const string AbsentArgs = "NULL";
    private const char ArgsPositionSeparator = '-';

    /// <summary>
    /// The patcher writes <c>exported.txt</c> as UTF-8, so we decode it strictly. An upload in the wrong
    /// encoding, or a corrupt one, then fails instead of decoding into nonsense that the diff would read
    /// as a source change and use to invalidate every Polish row. The rejection goes through the same
    /// truncation guard as any other parse failure.
    /// </summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public async Task<ParsedExport> ParseAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        List<ParsedExportRow> rows = [];
        List<ExportParseError> errors = [];

        await foreach (ParsedExportLine line in ParseLinesAsync(stream, cancellationToken))
        {
            if (line.Error is { } error)
            {
                errors.Add(error);
            }
            else
            {
                rows.Add(line.Row!);
            }
        }

        return new ParsedExport(rows, errors);
    }

    public async IAsyncEnumerable<ParsedExportLine> ParseLinesAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using StreamReader reader = new(stream, StrictUtf8, leaveOpen: true);

        int lineNumber = 0;
        while (true)
        {
            string? line;
            ExportParseError? decodeError = null;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken);
            }
            catch (DecoderFallbackException exception)
            {
                line = null;
                decodeError = new ExportParseError(lineNumber + 1, $"The upload is not valid UTF-8: {exception.Message}");
            }

            if (decodeError is not null)
            {
                yield return ParsedExportLine.ForError(decodeError);
                yield break;
            }

            if (line is null)
            {
                yield break;
            }

            lineNumber++;

            if (ShouldSkipLine(line))
            {
                continue;
            }

            yield return TryParseLine(line, out ParsedExportRow? row, out string? error)
                ? ParsedExportLine.ForRow(row!)
                : ParsedExportLine.ForError(new ExportParseError(lineNumber, error!));
        }
    }

    private static bool TryParseLine(string line, out ParsedExportRow? row, out string? error)
    {
        row = null;

        // Work from both ends, like the patcher does (#29, #106). file_id and gossip_id come first,
        // args_order, args_id and approved come last, and everything in between is the content, so it
        // may contain "||" and may end in any number of '|' (ADR-0042).
        if (!TranslationLineCarver.TryCarve(line, out CarvedTranslationLine? carved))
        {
            error = $"Expected at least {SeparatorCount} '{FieldSeparator}' separators outside the content.";
            return false;
        }

        if (!int.TryParse(carved.FileId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fileId))
        {
            error = $"File id '{carved.FileId}' is not a valid integer.";
            return false;
        }

        if (!long.TryParse(carved.GossipId, NumberStyles.Integer, CultureInfo.InvariantCulture, out long gossipId))
        {
            error = $"Gossip id '{carved.GossipId}' is not a valid integer.";
            return false;
        }

        if (!IsWellFormedArgs(carved.ArgsOrder))
        {
            error = DescribeMalformedArgs("args_order", carved.ArgsOrder);
            return false;
        }

        if (!IsWellFormedArgs(carved.ArgsId))
        {
            error = DescribeMalformedArgs("args_id", carved.ArgsId);
            return false;
        }

        // The content is unescaped last (ADR-0039), so the row carries the raw source text the DAT
        // really holds.
        string content = TranslationLineEscaper.Unescape(carved.Content);

        if (carved.SourceDigest is { } sourceDigest && !MatchesSourceDigest(content, carved, sourceDigest))
        {
            // The row carries a digest that does not match the row (ADR-0047 §2). That means the wrong
            // file was uploaded, or the two contexts compute the digest differently. It has to fail
            // loudly here instead of as 800,000 `source moved` warnings on players' machines.
            error = $"The source_digest column '{sourceDigest}' does not match the row's own text and argument columns.";
            return false;
        }

        row = new ParsedExportRow(
            FileId: fileId,
            GossipId: gossipId,
            Content: content,
            ArgsOrder: carved.ArgsOrder,
            ArgsId: carved.ArgsId,
            Approved: carved.Approved.Trim() == "1",
            SourceDigest: carved.SourceDigest);

        error = null;
        return true;
    }

    /// <summary>
    /// Computes the row's own <c>SourceHash</c> over the triple exactly as the catalog will store it,
    /// using <see cref="TranslationSource"/>'s rules, so an absent args column is
    /// <see langword="null"/> and never the literal text <c>NULL</c>, and compares it with the column in
    /// the file. Case is ignored: writers emit lower case and readers accept a hand-edited file.
    /// </summary>
    private static bool MatchesSourceDigest(string content, CarvedTranslationLine carved, string declaredDigest)
    {
        Result<TranslationSource> sourceResult = TranslationSource.Create(content, carved.ArgsOrder, carved.ArgsId);

        return sourceResult.IsSuccess
            && SourceHash.Compute(sourceResult.Value)
                .ToWireDigest()
                .Equals(declaredDigest, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An args column is either absent, meaning <c>NULL</c>, empty or blank, or a list of ASCII decimal
    /// numbers separated by <c>-</c>. Anything else rejects the row (ADR-0042) instead of being stored as
    /// it is, where it would be neither <see langword="null"/> nor a usable order and would still take
    /// part in the import diff (spec 0001).
    /// Whether those positions fit the fragment is the patcher's decision, not the catalog's: the TMS
    /// never sees the argument references.
    /// </summary>
    private static bool IsWellFormedArgs(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Equals(AbsentArgs, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // NumberStyles.None on purpose: no sign, because '-' is the separator, no spaces around the
        // number, ASCII digits only. A number too large for an int fails here as well.
        return value
            .Split(ArgsPositionSeparator)
            .All(position => int.TryParse(position, NumberStyles.None, CultureInfo.InvariantCulture, out _));
    }

    private static string DescribeMalformedArgs(string column, string value)
        => $"The {column} column '{value}' is neither NULL nor a '-' separated list of integers.";

    private static bool ShouldSkipLine(string line)
        => string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#');
}
