namespace LotroKoniecDev.TranslationSystem.API.Parsing;

internal interface ITranslationExportParser
{
    /// <summary>
    /// Rows and errors in file order (spec 0006). An invalid UTF-8 sequence produces one error and ends
    /// the stream.
    /// </summary>
    IAsyncEnumerable<ParsedExportLine> ParseLinesAsync(Stream stream, CancellationToken cancellationToken);
}
