namespace LotroKoniecDev.TranslationSystem.API.Parsing;

/// <summary>One parsed row of an uploaded export, in the verbatim exported representation.</summary>
/// <remarks>
/// Args columns are the raw <c>NULL</c>/<c>1-2-3</c> strings and <see cref="Content"/> keeps its
/// escaped <c>\r</c>/<c>\n</c> — the TMS stores and redistributes texts byte-for-byte; only the
/// patcher unescapes, when it injects into the DAT. <see cref="Approved"/> is retained for
/// format symmetry but the import ignores it: the export's column is a constant and TMS approval
/// state is owned by the editor loop, not the file.
/// </remarks>
public sealed record ParsedExportRow(
    int FileId,
    long GossipId,
    string Content,
    string ArgsOrder,
    string ArgsId,
    bool Approved);

/// <summary>A line that failed to parse, with its 1-based line number for the rejection message.</summary>
public sealed record ExportParseError(int LineNumber, string Message);

/// <summary>
/// The result of parsing an uploaded export: the well-formed rows plus every line that failed.
/// The import rejects the whole upload when <see cref="HasErrors"/> — on import a skipped line is
/// indistinguishable from a removed row, so partial parsing is unsafe (spec 0001, truncation guard).
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
