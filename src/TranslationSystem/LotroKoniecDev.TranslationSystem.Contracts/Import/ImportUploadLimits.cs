namespace LotroKoniecDev.TranslationSystem.Contracts.Import;

/// <summary>
/// The largest <c>exported.txt</c> an import accepts. Both ends of the upload read it from here so
/// they cannot disagree: the Frontend's Blazor SSR form (Kestrel request body and multipart limits)
/// and the TMS API's import endpoint (its own request body and multipart limits).
/// The export is about 80 MB today and grows as the game adds text, so the limit leaves a lot of room
/// while still stopping an upload that is far too large. The endpoint is admin-only and rate-limited
/// anyway.
/// </summary>
public static class ImportUploadLimits
{
    /// <summary>Maximum accepted <c>exported.txt</c> upload size, in bytes (256 MB).</summary>
    public const long MaxUploadBytes = 256L * 1024 * 1024;
}
