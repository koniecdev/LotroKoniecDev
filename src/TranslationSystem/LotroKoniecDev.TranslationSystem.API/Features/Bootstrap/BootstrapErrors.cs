using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Enums;
using LotroKoniecDev.TranslationSystem.API.Parsing;

namespace LotroKoniecDev.TranslationSystem.API.Features.Bootstrap;

/// <summary>
/// Polish-seed failures. A malformed seed file is an operator error to fix, so — like the import
/// (spec 0001, truncation guard) — a parse failure or a structurally invalid row rejects the whole
/// seed rather than silently dropping content. Unmatched (well-formed) rows are not failures: they
/// are reported in the summary, never inserted (#28 merge-only rule).
/// </summary>
internal static class BootstrapErrors
{
    public static Error PolishSeedParseFailed(int errorCount, ExportParseError first)
        => new("Bootstrap.PolishSeedParseFailed",
            $"The polish.txt seed has {errorCount} unparseable line(s); the seed is rejected. "
            + $"First failure — line {first.LineNumber}: {first.Message}",
            TypeOfError.DataConflict);

    public static Error PolishSeedInvalidRow(int fileId, long gossipId, string detail)
        => new("Bootstrap.PolishSeedInvalidRow",
            $"Seed row ({fileId}, {gossipId}) is invalid: {detail}",
            TypeOfError.DataConflict);
}
