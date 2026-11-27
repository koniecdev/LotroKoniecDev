namespace LotroKoniecDev.TranslationSystem.Persistence.SaveBudget;

/// <summary>
/// One permit an action takes before it saves a row (ADR-0072 §4). A fill takes one per row it saves.
/// </summary>
/// <param name="ReplacedPolish">The row it stands for already had Polish text.</param>
public sealed record TranslationSavePermit(Guid Id, bool ReplacedPolish);
