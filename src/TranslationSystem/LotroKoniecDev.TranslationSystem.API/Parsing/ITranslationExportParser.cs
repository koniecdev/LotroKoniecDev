namespace LotroKoniecDev.TranslationSystem.API.Parsing;

internal interface ITranslationExportParser
{
    /// <summary>
    /// Reads the whole upload into memory in one pass. The import streams it through
    /// <see cref="ParseLinesAsync"/> instead (spec 0006).
    /// </summary>
    Task<ParsedExport> ParseAsync(Stream stream, CancellationToken cancellationToken);

    /// <summary>
    /// Streams the upload line by line without holding it in memory (spec 0006). Each element is either
    /// a valid row or a parse error, in file order. UTF-8 is decoded as strictly as in
    /// <see cref="ParseAsync"/>: an invalid byte sequence produces one error element and ends the
    /// stream.
    /// </summary>
    IAsyncEnumerable<ParsedExportLine> ParseLinesAsync(Stream stream, CancellationToken cancellationToken);
}
