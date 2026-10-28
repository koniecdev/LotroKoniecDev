using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Application.Abstractions;

public interface ITranslationParser
{
    Result<TranslationParseResult> ParseFile(string filePath);

    Result<Translation> ParseLine(string line);
}
