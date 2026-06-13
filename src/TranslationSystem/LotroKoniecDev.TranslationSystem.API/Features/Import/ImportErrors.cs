using System.Globalization;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Enums;
using LotroKoniecDev.TranslationSystem.API.Parsing;

namespace LotroKoniecDev.TranslationSystem.API.Features.Import;

/// <summary>
/// Import-level failures (corrupt/truncated upload guards). Modelled as <see cref="TypeOfError.DataConflict"/>
/// so the API maps them to 422 Unprocessable Entity (spec 0001 contract).
/// </summary>
internal static class ImportErrors
{
    public static Error ParseFailed(int errorCount, ExportParseError first)
        => new("Import.ParseFailed",
            $"The upload has {errorCount} unparseable line(s); the import is rejected so a truncated file "
            + $"cannot masquerade as a mass removal. First failure — line {first.LineNumber}: {first.Message}",
            TypeOfError.DataConflict);

    public static Error InvalidRow(int fileId, long gossipId, string detail)
        => new("Import.InvalidRow",
            $"Row ({fileId}, {gossipId}) is invalid: {detail}",
            TypeOfError.DataConflict);

    public static Error DuplicateFragmentKey(int fileId, long gossipId)
        => new("Import.DuplicateFragmentKey",
            $"The upload contains more than one row for fragment ({fileId}, {gossipId}).",
            TypeOfError.DataConflict);

    public static Error EmptyUpload()
        => new("Import.EmptyUpload",
            "The upload contains no translatable rows; an empty or comments-only file is rejected "
            + "rather than marking the version processed with no content.",
            TypeOfError.DataConflict);

    public static Error MassRemovalBlocked(int removedCount, int activeCount, double removedFraction, double threshold)
        => new("Import.MassRemovalBlocked",
            $"The upload would remove {removedCount} of {activeCount} active row(s) "
            + $"({removedFraction.ToString("P0", CultureInfo.InvariantCulture)}), "
            + $"exceeding the {threshold.ToString("P0", CultureInfo.InvariantCulture)} safety threshold. "
            + "Re-upload with the override flag if this mass removal is intentional.",
            TypeOfError.DataConflict);
}
