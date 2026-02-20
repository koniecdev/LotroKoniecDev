using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Application.Parsers;

/// <summary>
/// Parses translation files in the LOTRO patcher format.
/// </summary>
/// <remarks>
/// File format: file_id||gossip_id||content||args_order||args_id||approved
/// Lines starting with # are comments, empty lines are ignored.
/// </remarks>
public sealed class TranslationFileParser : ITranslationParser
{
    private const string FieldSeparator = "||";
    private const int MinimumFieldCount = 5;

    public Result<IReadOnlyList<Translation>> ParseFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            return Result.Failure<IReadOnlyList<Translation>>(
                DomainErrors.Translation.FileNotFound(filePath));
        }

        List<Translation> translations = [];
        List<string> warnings = [];

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
            }
            else
            {
                warnings.Add(parseResult.Error.Message);
            }
        }

        // Sort by FileId then GossipId for optimal I/O during patching
        List<Translation> sortedTranslations = translations
            .OrderBy(t => t.FileId)
            .ThenBy(t => t.GossipId)
            .ToList();

        return Result.Success<IReadOnlyList<Translation>>(sortedTranslations);
    }

    public Result<Translation> ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return Result.Failure<Translation>(
                DomainErrors.Translation.InvalidFormat("Empty line"));
        }

        string[] parts = line.Split([FieldSeparator], StringSplitOptions.None);

        if (parts.Length < MinimumFieldCount)
        {
            return Result.Failure<Translation>(
                DomainErrors.Translation.InvalidFormat(
                    $"Expected at least {MinimumFieldCount} fields, got {parts.Length}"));
        }

        try
        {
            Translation translation = new()
            {
                FileId = int.Parse(parts[0]),
                GossipId = int.Parse(parts[1]),
                Content = UnescapeContent(parts[2]),
                ArgsOrder = ParseArgsArray(parts[3]),
                ArgsId = ParseArgsArray(parts[4])
            };

            return Result.Success(translation);
        }
        catch (FormatException ex)
        {
            return Result.Failure<Translation>(
                DomainErrors.Translation.ParseError(line, ex.Message));
        }
    }

    /// <summary>
    /// Determines if a line should be skipped (empty or comment).
    /// </summary>
    private static bool ShouldSkipLine(string line) =>
        string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#');

    /// <summary>
    /// Unescapes newline and carriage return sequences.
    /// </summary>
    private static string UnescapeContent(string content) =>
        content.Replace("\\r", "\r").Replace("\\n", "\n");

    /// <summary>
    /// Parses an argument array in format "1-2-3" to 0-indexed integers.
    /// </summary>
    /// <param name="value">The string value to parse.</param>
    /// <returns>Array of 0-indexed integers, or null if value is NULL or empty.</returns>
    private static int[]? ParseArgsArray(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Equals("NULL", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            // Format: "1-2-3" -> [0, 1, 2] (convert from 1-indexed to 0-indexed)
            return value
                .Split('-')
                .Select(s => int.Parse(s) - 1)
                .ToArray();
        }
        catch
        {
            return null;
        }
    }
}
