using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;

namespace LotroKoniecDev.TranslationSystem.API.Parsing;

/// <summary>
/// Parses an uploaded <c>exported.txt</c> in the LOTRO <c>||</c> contract
/// (<c>file_id||gossip_id||content||args_order||args_id||approved||source_digest</c>), carving fields
/// by anchoring from both ends (ADR-0042) and unfolding the content escape (ADR-0039) so the catalog
/// stores the raw source text rather than its file representation. The TMS owns its own parser;
/// golden fixtures + round-trip tests guard it against drift from the patcher's.
/// </summary>
/// <remarks>
/// The trailing <c>source_digest</c> (ADR-0047) is optional: a six-column upload — an older export,
/// a hand-made file — imports exactly as it always did. When the column IS present it is
/// <b>verified</b> against the row's own triple, so a wrong-file upload or a drift between the two
/// contexts' digest implementations is a per-row rejection here (ADR-0042) instead of a silent
/// artifact every player's patcher would then refuse.
/// </remarks>
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

        // The escape is unfolded last (ADR-0039), so the row hands out the raw source text the DAT
        // actually holds.
        string content = TranslationLineEscaper.Unescape(carved.Content);

        if (carved.SourceDigest is { } sourceDigest && !MatchesSourceDigest(content, carved, sourceDigest))
        {
            // The row claims a digest that is not the digest of the row (ADR-0047 §2). That is a
            // wrong-file upload or an implementation drift between the two contexts, and it must
            // fail loudly here rather than as 800k `source moved` warnings on players' boxes.
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
    /// Recomputes the row's own <c>SourceHash</c> over the triple exactly as the catalog will store
    /// it — <see cref="TranslationSource"/>'s normalization, so an absent args column is
    /// <see langword="null"/> and never the <c>NULL</c> literal — and compares it against the
    /// declared column. Case-insensitive: writers emit lowercase, readers forgive a hand-edited file.
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
