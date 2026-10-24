namespace LotroKoniecDev.TranslationSystem.Contracts.Import;

/// <summary>
/// Shared by the Frontend's upload form and the TMS API's import endpoint, so the two ends cannot
/// disagree. The export is about 80 MB today and grows with the game, so the limit leaves room while
/// still stopping an upload that is far too large.
/// </summary>
public static class ImportUploadLimits
{
    /// <summary>In bytes (256 MB).</summary>
    public const long MaxUploadBytes = 256L * 1024 * 1024;
}
