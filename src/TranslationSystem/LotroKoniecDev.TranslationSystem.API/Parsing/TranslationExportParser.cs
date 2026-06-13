using System.Globalization;
using System.Text;

namespace LotroKoniecDev.TranslationSystem.API.Parsing;

/// <summary>
/// Parses an uploaded <c>exported.txt</c> in the LOTRO <c>||</c> contract
/// (<c>file_id||gossip_id||content||args_order||args_id||approved</c>). The TMS owns its own
/// parser; golden fixtures + round-trip tests guard it against drift from the patcher's parser.
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

        using StreamReader reader = new(stream, StrictUtf8, leaveOpen: true);

        int lineNumber = 0;
        try
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                lineNumber++;

                if (ShouldSkipLine(line))
                {
                    continue;
                }

                if (TryParseLine(line, out ParsedExportRow? row, out string? error))
                {
                    rows.Add(row!);
                }
                else
                {
                    errors.Add(new ExportParseError(lineNumber, error!));
                }
            }
        }
        catch (DecoderFallbackException exception)
        {
            errors.Add(new ExportParseError(lineNumber + 1, $"The upload is not valid UTF-8: {exception.Message}"));
        }

        return new ParsedExport(rows, errors);
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
        // separator, so content may legally contain "||".
        string content = string.Join(FieldSeparator, parts[2..^3]);

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
