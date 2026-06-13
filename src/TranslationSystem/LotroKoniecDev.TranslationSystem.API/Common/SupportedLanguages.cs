namespace LotroKoniecDev.TranslationSystem.API.Common;

/// <summary>
/// The languages the catalog supports. Polish-only today; the single source of truth shared by the
/// editor queries, the import rebuild trigger and the distribution endpoint so they can't drift.
/// </summary>
internal static class SupportedLanguages
{
    public const string Polish = "pl";
}
