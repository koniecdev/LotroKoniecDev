namespace LotroKoniecDev.TranslationSystem.Persistence.SaveBudget;

/// <summary>
/// One account's saves inside the window, the save just recorded included.
/// </summary>
public sealed record TranslationSaveCounts(int Saves, int Overwrites);
