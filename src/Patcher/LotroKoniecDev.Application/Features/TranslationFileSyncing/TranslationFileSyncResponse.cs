namespace LotroKoniecDev.Application.Features.TranslationFileSyncing;

/// <summary>What the launch sync did with the local translation file.</summary>
public enum TranslationFileSyncOutcome
{
    /// <summary>A newer file was downloaded and written to disk.</summary>
    Updated,

    /// <summary>The server said the cached file is current (HTTP 304), so nothing was downloaded.</summary>
    UpToDate,

    /// <summary>
    /// The server could not be reached. The launch goes on with the local translation file, so it is
    /// never held up by the network.
    /// </summary>
    OfflineUsedCache,

    /// <summary>
    /// The downloaded file did not match the server's content hash, so it was refused. The launch goes
    /// on with the local translation file.
    /// </summary>
    IntegrityCheckFailedUsedCache,

    /// <summary>
    /// The download endpoint was not in the server's service document and no usable one was cached, so
    /// nothing was fetched. We never guess a path (#611). The launch goes on with the local translation
    /// file, and when there is none the launch path reports the missing file and exits 2.
    /// </summary>
    EndpointUnresolvedUsedCache
}

/// <summary>The result of a translation-file sync, with a short text the CLI can print.</summary>
public sealed record TranslationFileSyncResponse(TranslationFileSyncOutcome Outcome, string? Detail)
{
    public override string ToString() => Outcome switch
    {
        TranslationFileSyncOutcome.Updated => "Downloaded the latest translation file from the server.",
        TranslationFileSyncOutcome.UpToDate => "Translation file is already up to date.",
        TranslationFileSyncOutcome.OfflineUsedCache =>
            $"Could not reach the translation server; continuing with the local translation file. {Detail}".TrimEnd(),
        TranslationFileSyncOutcome.IntegrityCheckFailedUsedCache =>
            $"The downloaded translation file failed the integrity check and was rejected; continuing with the local translation file. {Detail}".TrimEnd(),
        TranslationFileSyncOutcome.EndpointUnresolvedUsedCache =>
            $"Could not work out where to download the translation file from; continuing with the local translation file. {Detail}".TrimEnd(),
        _ => Outcome.ToString()
    };
}
