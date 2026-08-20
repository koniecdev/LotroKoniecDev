using System.Globalization;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Application.Parsers;

/// <summary>
/// Reads a translation file in the LOTRO patcher format.
/// </summary>
/// <remarks>
/// The format is <c>file_id||gossip_id||content||args_order||args_id||approved||source_digest</c>.
/// A line starting with # is a comment, and empty lines are ignored.
/// The last field, <c>source_digest</c> (ADR-0047), is optional on read. A six-column line parses as
/// it always did and simply has no digest. It is the patcher's write guard, not this parser, that
/// turns such a row into a skipped one.
/// <see cref="TranslationLineCarver"/> cuts the fields out by working from both ends of the line
/// (ADR-0042), so the content may hold the separator and may end in any number of <c>|</c>.
/// The content arrives escaped (ADR-0039) and <see cref="TranslationLineEscaper"/> unescapes it, so
/// <see cref="Translation.Content"/> always holds the raw text that goes into the DAT.
/// </remarks>
public sealed class TranslationFileParser : ITranslationParser
{
    private const string FieldSeparator = "||";
    private const int MinSeparatorCount = 5;
    private const string AbsentArgs = "NULL";
    private const char ArgsPositionSeparator = '-';

    /// <summary>
    /// The same limit the TMS import uses (spec 0006). A completely broken file is rejected either
    /// way, and this only limits how many lines are quoted back. Without it, a 790k-row file gone bad
    /// would print one full-line warning per row.
    /// </summary>
    private const int MaxCollectedWarnings = 100;

    public Result<TranslationParseResult> ParseFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            return Result.Failure<TranslationParseResult>(
                DomainErrors.Translation.FileNotFound(filePath));
        }

        List<Translation> translations = [];
        List<string> warnings = [];
        int rejectedLineCount = 0;

        foreach (string line in File.ReadLines(filePath))
        {
            if (ShouldSkipLine(line))
            {
                continue;
            }

            Result<Translation> parseResult = ParseLine(line);

            if (parseResult.IsSuccess)
            {
                translations.Add(parseResult.Value);
                continue;
            }

            rejectedLineCount++;

            if (warnings.Count < MaxCollectedWarnings)
            {
                warnings.Add(parseResult.Error.Message);
            }
        }

        int quotedWarnings = warnings.Count;
        if (rejectedLineCount > quotedWarnings)
        {
            warnings.Add($"... and {rejectedLineCount - quotedWarnings} more rejected lines (only the first {MaxCollectedWarnings} are listed).");
        }

        // Sort by FileId and then GossipId, so patching reads the DAT in order.
        List<Translation> sortedTranslations = translations
            .OrderBy(t => t.FileId)
            .ThenBy(t => t.GossipId)
            .ToList();

        return Result.Success(new TranslationParseResult(sortedTranslations, warnings, rejectedLineCount));
    }

    public Result<Translation> ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return Result.Failure<Translation>(
                DomainErrors.Translation.InvalidFormat("Empty line"));
        }

        if (!TranslationLineCarver.TryCarve(line, out CarvedTranslationLine? carved))
        {
            return Result.Failure<Translation>(
                DomainErrors.Translation.InvalidFormat(
                    $"expected at least {MinSeparatorCount} '{FieldSeparator}' separators outside the content, in line '{line}'"));
        }

        if (!int.TryParse(carved.FileId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fileId))
        {
            return Result.Failure<Translation>(
                DomainErrors.Translation.ParseError(line, $"File id '{carved.FileId}' is not a valid integer."));
        }

        if (!ulong.TryParse(carved.GossipId, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong gossipId))
        {
            return Result.Failure<Translation>(
                DomainErrors.Translation.ParseError(line, $"Gossip id '{carved.GossipId}' is not a valid integer."));
        }

        if (!TryParseArgsArray(carved.ArgsOrder, out int[]? argsOrder))
        {
            return Result.Failure<Translation>(
                DomainErrors.Translation.ParseError(line, DescribeMalformedArgs("args_order", carved.ArgsOrder)));
        }

        if (!TryParseArgsArray(carved.ArgsId, out int[]? argsId))
        {
            return Result.Failure<Translation>(
                DomainErrors.Translation.ParseError(line, DescribeMalformedArgs("args_id", carved.ArgsId)));
        }

        Translation translation = new()
        {
            FileId = fileId,
            GossipId = gossipId,
            Content = TranslationLineEscaper.Unescape(carved.Content),
            ArgsOrder = argsOrder,
            ArgsId = argsId,
            IsApproved = carved.Approved == "1",
            // A missing digest is not a parse error (ADR-0047 §3). Rejecting it here would turn a
            // file that is six columns throughout into NoTranslationsEveryLineRejected, which the
            // launch path reads as RepatchFailed and refuses to start the game on. The write guard
            // skips such rows instead, reports them, and lets the launch go ahead.
            SourceDigest = carved.SourceDigest
        };

        return Result.Success(translation);
    }

    private static bool ShouldSkipLine(string line) =>
        string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#');

    /// <summary>
    /// Reads an argument column such as "1-2-3" into 0-indexed integers. A column that is absent, so
    /// <c>NULL</c>, empty or blank, succeeds and gives <see langword="null"/> arguments. Anything else
    /// that is not a list of ASCII decimal numbers separated by <c>-</c> fails, so the caller rejects
    /// and reports the row instead of patching it without its argument order (ADR-0042).
    /// Whether those positions fit the fragment is checked later in
    /// <c>Fragment.TryReorderArgRefs</c>, the only place that knows how many arguments there are.
    /// </summary>
    private static bool TryParseArgsArray(string value, out int[]? args)
    {
        args = null;

        if (string.IsNullOrWhiteSpace(value) ||
            value.Equals(AbsentArgs, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string[] positions = value.Split(ArgsPositionSeparator);
        int[] parsed = new int[positions.Length];

        for (int index = 0; index < positions.Length; index++)
        {
            // NumberStyles.None on purpose: no sign, because '-' is the separator, no spaces around
            // the number, ASCII digits only. A number too large for an int fails here as well.
            if (!int.TryParse(positions[index], NumberStyles.None, CultureInfo.InvariantCulture, out int position))
            {
                return false;
            }

            parsed[index] = position - 1;
        }

        args = parsed;
        return true;
    }

    private static string DescribeMalformedArgs(string column, string value)
        => $"The {column} column '{value}' is neither NULL nor a '-' separated list of integers.";
}
