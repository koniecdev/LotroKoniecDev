namespace LotroKoniecDev.TranslationSystem.API.Parsing;

/// <summary>One parsed row of an uploaded export.</summary>
/// <remarks>
/// The args columns are the raw <c>NULL</c> or <c>1-2-3</c> strings, because they hold nothing that
/// needs escaping. <see cref="Content"/> is the raw text: the parser unescapes what the file carries
/// (ADR-0039) and the serializer escapes it again on the way out, so the escape never reaches the
/// catalog.
/// <see cref="Approved"/> is kept so the shape matches the file, but the import ignores it. The column
/// in an export is always the same value, and approval in the TMS belongs to the editor, not to the
/// file.
/// <see cref="SourceDigest"/> is the seventh column when the upload has one (ADR-0047). The parser has
/// already checked it against the row itself, so here it is only information. It is
/// <see langword="null"/> for a six-column upload, which imports just as well.
/// </remarks>
public sealed record ParsedExportRow(
    int FileId,
    long GossipId,
    string Content,
    string ArgsOrder,
    string ArgsId,
    bool Approved,
    string? SourceDigest = null);

/// <summary>A line that could not be parsed, with its line number for the message.</summary>
public sealed record ExportParseError(int LineNumber, string Message);

/// <summary>
/// One item of a streamed export parse (spec 0006). Exactly one of <see cref="Row"/>, a valid line, or
/// <see cref="Error"/>, a line that failed, is set. Each consumer decides what to do with them: the
/// import stops collecting errors after a limit, while
/// <see cref="ITranslationExportParser.ParseAsync"/> keeps them all.
/// </summary>
public readonly record struct ParsedExportLine
{
    public ParsedExportRow? Row { get; }
    public ExportParseError? Error { get; }

    private ParsedExportLine(ParsedExportRow? row, ExportParseError? error)
    {
        Row = row;
        Error = error;
    }

    public static ParsedExportLine ForRow(ParsedExportRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new ParsedExportLine(row, null);
    }

    public static ParsedExportLine ForError(ExportParseError error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return new ParsedExportLine(null, error);
    }
}

/// <summary>
/// The result of parsing an uploaded export: the valid rows plus every line that failed. The import
/// rejects the whole upload when <see cref="HasErrors"/> is true, because a skipped line looks exactly
/// like a removed row, so importing only part of a file is not safe (spec 0001, truncation guard).
/// </summary>
public sealed class ParsedExport
{
    public IReadOnlyList<ParsedExportRow> Rows { get; }
    public IReadOnlyList<ExportParseError> Errors { get; }

    public bool HasErrors => Errors.Count > 0;

    public ParsedExport(IReadOnlyList<ParsedExportRow> rows, IReadOnlyList<ExportParseError> errors)
    {
        Rows = rows;
        Errors = errors;
    }
}
