namespace LotroKoniecDev.TranslationSystem.API;

/// <summary>
/// Event ID ranges: TranslationSystem 1000-1999, AuthSystem 2000-2999, Shared 3000-3999.
/// </summary>
internal static class EventIds
{
    // Exception Handlers (1100-1199)
    public const int ArgumentException = 1100;
    public const int BadHttpRequest = 1110;
    public const int ConcurrencyConflict = 1120;
    public const int ValidationFailure = 1140;
    public const int UnhandledException = 1150;

    // Import (1200-1299)
    public const int ImportPassesCompleted = 1200;

    // Middleware (1300-1399)
    public const int UnauthorizedAccessAttempt = 1300;
    public const int ForbiddenAccessAttempt = 1301;
    public const int TranslatorProvisioningSkipped = 1302;

    // Background workers (1400-1499)
    public const int TranslationFileRebuildCompleted = 1400;
    public const int TranslationFileRebuildFailed = 1401;
    public const int TranslationFileFormatUpgradeStarted = 1402;
    public const int TranslationFileFormatUpgradeCompleted = 1403;
    public const int TranslationFileFormatUpgradeFailed = 1404;

    // GDPR (1500-1599)
    public const int GdprContributionExportRequested = 1500;
    public const int GdprContributionExportCompleted = 1501;

    // Translation file downloads (1600-1699)
    public const int TranslationFileCopyWritten = 1600;
    public const int TranslationFileCopyRemovalFailed = 1601;

    // Account events from the AuthSystem (1700-1799)
    public const int AccountConsumerStarted = 1700;
    public const int AccountConsumerConnectFailed = 1701;
    public const int TranslatorProfileErased = 1702;
    public const int AccountConsumerUnknownMessageType = 1703;
    public const int AccountConsumerPoisonMessage = 1704;
    public const int AccountConsumerEraseFailed = 1705;
    public const int AccountConsumerUnexpectedError = 1706;
    public const int AccountConsumerTeardownWarning = 1707;
    public const int AccountConsumerDetached = 1708;
    public const int AccountConsumerRefused = 1709;
}
