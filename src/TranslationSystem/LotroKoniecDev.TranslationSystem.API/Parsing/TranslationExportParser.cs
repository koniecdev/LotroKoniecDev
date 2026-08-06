using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace LotroKoniecDev.TranslationSystem.API.Parsing;

/// <summary>
/// Parses an uploaded <c>exported.txt</c> in the LOTRO <c>||</c> contract
/// (<c>file_id||gossip_id||content||args_order||args_id||approved</c>), carving fields by anchoring
/// from both ends (ADR-0042) and unfolding the content escape (ADR-0039) so the catalog stores the
/// raw source text rather than its file representation. The TMS owns its own parser; golden fixtures
/// + round-trip tests guard it against drift from the patcher's.
/// </summary>
internal sealed class TranslationExportParser : ITranslationExportParser
{
    private const string FieldSeparator = "||";
    private const int SeparatorCount = 5;
    private const string AbsentArgs = "NULL";
    private const char ArgsPositionSeparator = '-';

    /// <summary>
    /// The patcher writes <c>exported.txt</c> as UTF-8; decode it strictly. A wrong-charset or
    /// corrupt upload then throws instead of silently mis-decoding into garbage content that the
    /// diff would treat as a source change and mass-invalidate every Polish row — the rejection
    /// routes through the same truncation guard as a structural parse failure.
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

        // Anchor from both ends (matches the patcher, #29/#106): file_id, gossip_id lead;
        // args_order, args_id, approved trail; everything between is content, so it may legally
        // contain "||" and may end in any run of '|' (ADR-0042).
        if (!TranslationLineCarver.TryCarve(line, out CarvedTranslationLine? carved))
        {
            error = $"Expected {SeparatorCount} '{FieldSeparator}' separators outside the content.";
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

        row = new ParsedExportRow(
            FileId: fileId,
            GossipId: gossipId,
            // The escape is unfolded last (ADR-0039), so the row hands out the raw source text the
            // DAT actually holds.
            Content: TranslationLineEscaper.Unescape(carved.Content),
            ArgsOrder: carved.ArgsOrder,
            ArgsId: carved.ArgsId,
            Approved: carved.Approved.Trim() == "1");

        error = null;
        return true;
    }

    /// <summary>
    /// An args column is either absent (<c>NULL</c>, empty or blank) or a <c>-</c>-separated list of
    /// ASCII decimal integers. Anything else rejects the row (ADR-0042) rather than being stored
    /// verbatim, where it would be neither <see langword="null"/> nor a usable order and would still
    /// take part in the import diff (spec 0001). Whether the positions fit the fragment is the
    /// patcher's call, not the catalog's — the TMS never sees the argument references.
    /// </summary>
    private static bool IsWellFormedArgs(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Equals(AbsentArgs, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // NumberStyles.None on purpose: no sign (the '-' is the separator), no surrounding
        // whitespace, ASCII digits only. An overflowing position fails here too.
        return value
            .Split(ArgsPositionSeparator)
            .All(position => int.TryParse(position, NumberStyles.None, CultureInfo.InvariantCulture, out _));
    }

    private static string DescribeMalformedArgs(string column, string value)
        => $"The {column} column '{value}' is neither NULL nor a '-' separated list of integers.";

    private static bool ShouldSkipLine(string line)
        => string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#');
}
