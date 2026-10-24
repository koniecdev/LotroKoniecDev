namespace LotroKoniecDev.TranslationSystem.Contracts.Import;

/// <summary>
/// What an import did (spec 0001). <paramref name="Invalidated"/> counts source-changed rows that
/// already carried Polish. <paramref name="Echoed"/> counts our own Polish coming back from a patched
/// DAT (spec 0012); those rows are already in <paramref name="Unchanged"/> or among the restored rows.
/// <paramref name="Warnings"/> are notices, not errors, such as restored rows.
/// </summary>
public sealed record ImportSummary(
    int Added,
    int SourceChanged,
    int Invalidated,
    int Removed,
    int Unchanged,
    int Echoed,
    IReadOnlyCollection<string> Warnings);
