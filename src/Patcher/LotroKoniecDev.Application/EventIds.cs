namespace LotroKoniecDev.Application;

/// <summary>
/// Event ID ranges: TranslationSystem 1000–1999, AuthSystem 2000–2999, Shared 3000–3999,
/// Hateoas 4000–4999, Patcher 5000–5999 (Application 5000–5499, Infrastructure 5500–5999).
/// </summary>
internal static class EventIds
{
    // Update Checking (5000–5099)
    public const int ForumFetchFailed = 5000;
    public const int ForumVersionParseFailed = 5001;

    // Game Launching (5100–5199)
    public const int LaunchStarted = 5100;
    public const int LaunchDatFilePath = 5101;
    public const int LaunchTranslationFilePath = 5102;
    public const int LaunchGameVersionFilePath = 5103;
    public const int LaunchBlockedGameAlreadyRunning = 5104;
    public const int LaunchComputingTranslationHash = 5105;
    public const int LaunchComputeHashFailed = 5106;
    public const int LaunchCurrentTranslationHash = 5107;
    public const int LaunchReadingStoredVersion = 5108;
    public const int LaunchReadStoredVersionFailed = 5109;
    public const int LaunchStoredVersionInfo = 5110;
    public const int LaunchTranslationChangeEvaluated = 5111;
    public const int LaunchPatchingTranslationChanged = 5112;
    public const int LaunchApplyTranslationsFailed = 5113;
    public const int LaunchTranslationsPatched = 5114;
    public const int LaunchReadingDatVnum = 5115;
    public const int LaunchReadVersionFailed = 5116;
    public const int LaunchDatVnumRead = 5117;
    public const int LaunchSaveVersionFailed = 5118;
    public const int LaunchVersionSaved = 5119;
    public const int LaunchSkippedTranslationUnchanged = 5120;
    public const int LaunchStartingGame = 5121;
    public const int LaunchGameLaunchFailed = 5122;
    public const int LaunchLauncherStarted = 5123;
    public const int LaunchEnded = 5124;
    public const int LaunchPatchWarning = 5125;
}
