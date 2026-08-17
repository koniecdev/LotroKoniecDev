namespace LotroKoniecDev.Application.Features.Patching;

/// <summary>
/// Contains summary information about a patch operation.
/// </summary>
/// <param name="SourceMovedTranslations">
/// Rows the write guard refused because the DAT no longer holds the English they were translated
/// from (ADR-0047 §3). A non-zero count on update day is the mechanism working, not a fault: those
/// fragments keep the game's own current text instead of a translation that would describe the old
/// one. Counted inside <see cref="SkippedTranslations"/>.
/// </param>
/// <param name="MissingSourceDigestTranslations">
/// Rows that carried no <c>source_digest</c> column, so nothing could be verified against the DAT.
/// A whole six-column translation file lands entirely here — it parses, it does not patch, and the
/// launch still proceeds. Counted inside <see cref="SkippedTranslations"/>.
/// </param>
public sealed record PatchSummaryResponse(
    int TotalTranslations,
    int AppliedTranslations,
    int SkippedTranslations,
    List<string> Warnings,
    int SourceMovedTranslations = 0,
    int MissingSourceDigestTranslations = 0);
