namespace LotroKoniecDev.TranslationSystem.API.Parsing;

internal interface ITranslationExportParser
{
    /// <summary>
    /// Holds the whole upload in memory; the import streams through <see cref="ParseLinesAsync"/>
    /// instead (spec 0006).
    /// </summary>
    Task<ParsedExport> ParseAsync(Stream stream, CancellationToken cancellationToken);

    /// <summary>
    /// Rows and errors in file order (spec 0006). An invalid UTF-8 sequence produces one error and ends
    /// the stream.
    /// </summary>
    IAsyncEnumerable<ParsedExportLine> ParseLinesAsync(Stream stream, CancellationToken cancellationToken);
}
