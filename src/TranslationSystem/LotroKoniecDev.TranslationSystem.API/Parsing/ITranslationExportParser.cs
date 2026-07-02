namespace LotroKoniecDev.TranslationSystem.API.Parsing;

internal interface ITranslationExportParser
{
    /// <summary>
    /// Parses the whole upload into memory. Kept for the bootstrap seeder's much smaller
    /// <c>polish.txt</c> (spec 0006) — the import streams via
    /// <see cref="ParseLinesAsync"/> instead.
    /// </summary>
    Task<ParsedExport> ParseAsync(Stream stream, CancellationToken cancellationToken);

    /// <summary>
    /// Streams the upload line by line without materializing it (spec 0006): each element is a
    /// well-formed row or a parse error, in file order. Strict-UTF-8 handling matches
    /// <see cref="ParseAsync"/> — an invalid byte sequence yields one error element and ends the
    /// stream.
    /// </summary>
    IAsyncEnumerable<ParsedExportLine> ParseLinesAsync(Stream stream, CancellationToken cancellationToken);
}
