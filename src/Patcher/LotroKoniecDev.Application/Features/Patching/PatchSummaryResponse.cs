namespace LotroKoniecDev.Application.Features.Patching;

/// <summary>
/// What a patch run did, in numbers.
/// </summary>
/// <param name="SourceMovedTranslations">
/// Rows the write guard refused because the DAT no longer holds the English they were translated from
/// (ADR-0047 §3). On update day a number above zero means the guard is working, not that something
/// broke: those fragments keep the game's current text instead of a translation of the old one.
/// They are also counted in <see cref="SkippedTranslations"/>.
/// </param>
/// <param name="MissingSourceDigestTranslations">
/// Rows with no <c>source_digest</c> column, so there was nothing to check the DAT against. A
/// translation file that is six columns throughout ends up entirely here: it parses, it patches
/// nothing, and the launch still goes ahead.
/// They are also counted in <see cref="SkippedTranslations"/>.
/// </param>
public sealed record PatchSummaryResponse(
    int TotalTranslations,
    int AppliedTranslations,
    int SkippedTranslations,
    List<string> Warnings,
    int SourceMovedTranslations = 0,
    int MissingSourceDigestTranslations = 0);
