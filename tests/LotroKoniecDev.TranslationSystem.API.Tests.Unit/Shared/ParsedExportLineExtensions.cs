using LotroKoniecDev.TranslationSystem.API.Parsing;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Shared;

internal sealed record CollectedLines(IReadOnlyList<ParsedExportRow> Rows, IReadOnlyList<ExportParseError> Errors);

internal static class ParsedExportLineExtensions
{
    extension(IAsyncEnumerable<ParsedExportLine> lines)
    {
        public async Task<CollectedLines> CollectAsync()
        {
            List<ParsedExportRow> rows = [];
            List<ExportParseError> errors = [];

            await foreach (ParsedExportLine line in lines)
            {
                if (line.Error is { } error)
                {
                    errors.Add(error);
                }
                else
                {
                    rows.Add(line.Row!);
                }
            }

            return new CollectedLines(rows, errors);
        }
    }
}
