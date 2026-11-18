namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// The files the desktop app's Patch and Play keep under the exe's <c>data</c> folder, which only
/// administrators can change once the app is installed (ADR-0069 amendment of 2026-11-18, item 2).
/// The patcher writes its small files next to the translation file it reads, so every patch reads
/// <see cref="WorkingTranslationFilePath"/>, a copy the app makes, and never the player's own file.
/// </summary>
public interface IDesktopPatchWorkspace
{
    /// <summary>Where the download from the server goes, with its <c>.etag</c> and <c>.endpoint</c>.</summary>
    string DownloadedTranslationFilePath { get; }

    /// <summary>The copy every patch reads, with its <c>.ledger</c> and <c>.iterations</c> next to it.</summary>
    string WorkingTranslationFilePath { get; }

    string GameVersionFilePath { get; }

    /// <summary>
    /// Copies the file over the working copy. A missing file gives
    /// <c>DesktopPatchWorkspace.NotFound</c> and leaves the working copy as it was.
    /// </summary>
    Result UseTranslationFile(string sourcePath);

    /// <summary>
    /// Deletes the stored game version when it was saved for another game file than this one. The
    /// next patch then applies the whole file instead of trusting a hash that belongs to another game.
    /// </summary>
    Result ResetPatchStateIfGameFileChanged(string datFilePath);
}
