using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace LotroKoniecDev.TranslationSystem.API.Parsing;

/// <summary>
/// Parses an uploaded <c>exported.txt</c> in the LOTRO <c>||</c> contract
/// (<c>file_id||gossip_id||content||args_order||args_id||approved</c>), unfolding the content escape
/// (ADR-0039) so the catalog stores the raw source text rather than its file representation. The TMS
/// owns its own parser; golden fixtures + round-trip tests guard it against drift from the patcher's.
/// </summary>
internal sealed class TranslationExportParser : ITranslationExportParser
{
    private const string FieldSeparator = "||";
    private const int MinimumFieldCount = 6;

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

        string[] parts = line.Split(FieldSeparator, StringSplitOptions.None);

        if (parts.Length < MinimumFieldCount)
        {
            error = $"Expected at least {MinimumFieldCount} fields, got {parts.Length}.";
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int fileId))
        {
            error = $"File id '{parts[0]}' is not a valid integer.";
            return false;
        }

        if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long gossipId))
        {
            error = $"Gossip id '{parts[1]}' is not a valid integer.";
            return false;
        }

        // Anchor from both ends (matches the patcher, #29/#106): file_id, gossip_id lead;
        // args_order, args_id, approved trail; everything between is content re-joined with the
        // separator, so content may legally contain "||". The escape is unfolded last (ADR-0039),
        // so the row hands out the raw source text the DAT actually holds.
        string content = TranslationLineEscaper.Unescape(string.Join(FieldSeparator, parts[2..^3]));

        row = new ParsedExportRow(
            FileId: fileId,
            GossipId: gossipId,
            Content: content,
            ArgsOrder: parts[^3],
            ArgsId: parts[^2],
            Approved: parts[^1].Trim() == "1");

        error = null;
        return true;
    }

    private static bool ShouldSkipLine(string line)
        => string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#');
}
