using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Application.Abstractions;

public interface ITranslationParser
{
    /// <summary>
    /// Reads a translation file and returns the rows that parsed, plus one warning per rejected line.
    /// </summary>
    Result<TranslationParseResult> ParseFile(string filePath);

    Result<Translation> ParseLine(string line);
}
