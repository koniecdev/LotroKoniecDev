namespace LotroKoniecDev.Application.Features.TranslationFileSyncing;

/// <summary>What the launch sync did with the local translation file.</summary>
public enum TranslationFileSyncOutcome
{
    /// <summary>A newer file was downloaded and written to disk.</summary>
    Updated,

    /// <summary>The server confirmed the cached file is current (HTTP 304) — nothing downloaded.</summary>
    UpToDate,

    /// <summary>The server was unreachable; the launch continues with the local translation file so it is never blocked on the network.</summary>
    OfflineUsedCache,

    /// <summary>The downloaded file did not match the server's content hash and was rejected; the launch continues with the local translation file.</summary>
    IntegrityCheckFailedUsedCache
}

/// <summary>The result of a translation-file sync, with a human-readable summary for the CLI report.</summary>
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
        _ => Outcome.ToString()
    };
}
