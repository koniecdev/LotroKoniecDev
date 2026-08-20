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
}
