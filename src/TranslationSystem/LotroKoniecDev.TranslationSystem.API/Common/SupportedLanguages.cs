namespace LotroKoniecDev.TranslationSystem.API.Common;

/// <summary>
/// The languages the catalog supports. Today that is Polish only. The editor queries, the import
/// rebuild trigger and the distribution endpoint all read it from here, so they cannot disagree.
/// </summary>
internal static class SupportedLanguages
{
    public const string Polish = "pl";
}
