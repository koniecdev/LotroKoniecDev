namespace LotroKoniecDev.TranslationSystem.API.Parsing;

internal interface ITranslationExportParser
{
    Task<ParsedExport> ParseAsync(Stream stream, CancellationToken cancellationToken);
}
