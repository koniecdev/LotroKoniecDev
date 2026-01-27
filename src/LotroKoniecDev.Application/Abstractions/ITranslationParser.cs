using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// Defines the contract for parsing translation files.
/// </summary>
public interface ITranslationParser
{
    /// <summary>
    /// Parses a translation file and returns all valid translations.
    /// </summary>
    /// <param name="filePath">Path to the translation file.</param>
    /// <returns>Result containing the list of translations or an error.</returns>
    Result<IReadOnlyList<Translation>> ParseFile(string filePath);

    /// <summary>
    /// Parses a single translation line.
    /// </summary>
    /// <param name="line">The line to parse.</param>
    /// <returns>Result containing the translation or an error.</returns>
    Result<Translation> ParseLine(string line);
}
