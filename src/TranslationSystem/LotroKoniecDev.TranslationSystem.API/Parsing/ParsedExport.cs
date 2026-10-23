namespace LotroKoniecDev.TranslationSystem.API.Parsing;

/// <remarks>
/// <see cref="Content"/> is already unescaped (ADR-0039); the args columns keep their file form.
/// <see cref="Approved"/> only mirrors the file: the import ignores it, because approval belongs to the
/// editor. <see cref="SourceDigest"/> was already checked against the row, and is
/// <see langword="null"/> for a six-column upload (ADR-0047).
/// </remarks>
public sealed record ParsedExportRow(
    int FileId,
    long GossipId,
    string Content,
    string ArgsOrder,
    string ArgsId,
    bool Approved,
    string? SourceDigest = null);

public sealed record ExportParseError(int LineNumber, string Message);

/// <summary>
/// Exactly one of <see cref="Row"/> and <see cref="Error"/> is set (spec 0006).
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
/// The valid rows plus every failed line. The import streams through
/// <see cref="ITranslationExportParser.ParseLinesAsync"/> instead, with the same rule: one failed line
/// rejects the upload, because a skipped line looks like a removed row (spec 0001).
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
