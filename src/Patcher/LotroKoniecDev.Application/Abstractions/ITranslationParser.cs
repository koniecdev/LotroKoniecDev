using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// Defines the contract for parsing translation files.
/// </summary>
public interface ITranslationParser
{
    /// <summary>
    /// Parses a translation file into its valid translations plus a warning per rejected line.
    /// </summary>
    /// <param name="filePath">Path to the translation file.</param>
    /// <returns>Result containing the parsed translations and warnings, or an error.</returns>
    Result<TranslationParseResult> ParseFile(string filePath);

    /// <summary>
    /// Parses a single translation line.
    /// </summary>
    /// <param name="line">The line to parse.</param>
    /// <returns>Result containing the translation or an error.</returns>
    Result<Translation> ParseLine(string line);
}
