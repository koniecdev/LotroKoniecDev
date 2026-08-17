using System.Globalization;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Application.Parsers;

/// <summary>
/// Parses translation files in the LOTRO patcher format.
/// </summary>
/// <remarks>
/// File format: file_id||gossip_id||content||args_order||args_id||approved||source_digest
/// Lines starting with # are comments, empty lines are ignored.
/// The trailing <c>source_digest</c> (ADR-0047) is optional on read: a six-column line parses
/// exactly as it always did and simply carries no digest, which the patcher's write guard — not
/// this parser — turns into a skipped row.
/// Fields are carved by <see cref="TranslationLineCarver"/>, which anchors from both ends
/// (ADR-0042), so content may contain the separator and may end in any run of <c>|</c>.
/// Content arrives escaped (ADR-0039) and is unfolded by <see cref="TranslationLineEscaper"/>, so
/// <see cref="Translation.Content"/> always carries the raw text about to be written into the DAT.
/// </remarks>
public sealed class TranslationFileParser : ITranslationParser
{
    private const string FieldSeparator = "||";
    private const int MinSeparatorCount = 5;
    private const string AbsentArgs = "NULL";
    private const char ArgsPositionSeparator = '-';

    /// <summary>
    /// Mirrors the TMS import's own cap (spec 0006): a wholly corrupt file is rejected either way,
    /// and the cap only bounds how many lines are quoted back. Without it a 790k-row file gone bad
    /// would print one full-line warning per row to the console.
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

        // Sort by FileId then GossipId for optimal I/O during patching
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
            // A missing digest is NOT a parse failure (ADR-0047 §3). Rejecting here would turn a
            // wholly six-column file into NoTranslationsEveryLineRejected, which the launch path
            // maps to RepatchFailed and refuses to start the game on — the guard skips such rows
            // instead, reports them, and lets the launch through.
            SourceDigest = carved.SourceDigest
        };

        return Result.Success(translation);
    }

    /// <summary>
    /// Determines if a line should be skipped (empty or comment).
    /// </summary>
    private static bool ShouldSkipLine(string line) =>
        string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#');

    /// <summary>
    /// Parses an argument column in format "1-2-3" to 0-indexed integers. An absent column
    /// (<c>NULL</c>, empty or blank) yields <see langword="null"/> arguments and succeeds; anything
    /// else that is not a <c>-</c>-separated list of ASCII decimal integers fails, so the caller
    /// rejects and reports the row rather than silently patching it without its argument order
    /// (ADR-0042). Whether the positions FIT the fragment is checked downstream by
    /// <c>Fragment.TryReorderArgRefs</c>, which is the only place that knows how many there are.
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
            // NumberStyles.None on purpose: no sign (the '-' is the separator), no surrounding
            // whitespace, ASCII digits only. An overflowing position fails here too.
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
