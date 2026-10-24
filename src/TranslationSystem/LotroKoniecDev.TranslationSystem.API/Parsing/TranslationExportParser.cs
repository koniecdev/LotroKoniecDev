using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;

namespace LotroKoniecDev.TranslationSystem.API.Parsing;

/// <summary>
/// Reads an uploaded <c>exported.txt</c>
/// (<c>file_id||gossip_id||content||args_order||args_id||approved||source_digest</c>): carves the line
/// (ADR-0042) and unescapes the content (ADR-0039), so the catalog stores the raw text. Golden fixtures
/// and round-trip tests keep it in step with the patcher's own parser.
/// </summary>
/// <remarks>
/// <c>source_digest</c> (ADR-0047) is optional. When present it is checked against the row, so a wrong
/// file, or digest code that differs between the contexts, fails here instead of producing an artifact
/// every player's patcher would refuse.
/// </remarks>
internal sealed class TranslationExportParser : ITranslationExportParser
{
    private const string FieldSeparator = "||";
    private const int SeparatorCount = 5;
    private const string AbsentArgs = "NULL";
    private const char ArgsPositionSeparator = '-';

    /// <summary>
    /// The patcher writes UTF-8. Strict decoding makes a wrong or corrupt upload fail instead of
    /// decoding into nonsense the diff would read as source changes, invalidating every Polish row.
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

        string content = TranslationLineEscaper.Unescape(carved.Content);

        if (carved.SourceDigest is { } sourceDigest && !MatchesSourceDigest(content, carved, sourceDigest))
        {
            // A wrong file or a digest drift between the contexts (ADR-0047 §2): fail here, not as
            // 800,000 `source moved` warnings on players' machines.
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
    /// Hashes the triple through <see cref="TranslationSource"/>, exactly as the catalog will store it.
    /// Case is ignored, so a hand-edited file still matches.
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
    /// Absent (<c>NULL</c> or blank) or ASCII numbers separated by <c>-</c>; anything else rejects the row
    /// (ADR-0042). Whether the positions fit the fragment is the patcher's call: the TMS never sees the
    /// argument references.
    /// </summary>
    private static bool IsWellFormedArgs(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Equals(AbsentArgs, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // NumberStyles.None: no sign ('-' is the separator), no spaces, ASCII digits only.
        return value
            .Split(ArgsPositionSeparator)
            .All(position => int.TryParse(position, NumberStyles.None, CultureInfo.InvariantCulture, out _));
    }

    private static string DescribeMalformedArgs(string column, string value)
        => $"The {column} column '{value}' is neither NULL nor a '-' separated list of integers.";

    private static bool ShouldSkipLine(string line)
        => string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#');
}
