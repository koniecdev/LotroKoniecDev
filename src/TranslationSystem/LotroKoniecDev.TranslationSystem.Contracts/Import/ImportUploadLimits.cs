namespace LotroKoniecDev.TranslationSystem.Contracts.Import;

/// <summary>
/// Upload-size ceiling for an <c>exported.txt</c> import, shared by both ends of the upload so they
/// never disagree: the Frontend's Blazor SSR form (its Kestrel request-body + multipart form limits)
/// and the TMS API's import endpoint (its per-endpoint request-body + multipart form limits). The
/// export is ~80 MB today and grows as the game adds text, so the ceiling keeps a wide headroom while
/// still bounding an oversized/abusive upload — the endpoint is admin-only and rate-limited.
/// </summary>
public static class ImportUploadLimits
{
    /// <summary>Maximum accepted <c>exported.txt</c> upload size, in bytes (256 MB).</summary>
    public const long MaxUploadBytes = 256L * 1024 * 1024;
}
