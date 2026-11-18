namespace LotroKoniecDev.Infrastructure;

/// <summary>
/// Event ID ranges: TranslationSystem 1000-1999, AuthSystem 2000-2999, Shared 3000-3999,
/// Hateoas 4000-4999, Patcher 5000-5999 (Application 5000-5499, Infrastructure 5500-5999).
/// </summary>
internal static class EventIds
{
    // Diagnostics (5500–5599)
    public const int GameProcessCheckFailed = 5500;
}
