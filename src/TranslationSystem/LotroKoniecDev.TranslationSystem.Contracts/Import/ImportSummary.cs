namespace LotroKoniecDev.TranslationSystem.Contracts.Import;

/// <summary>
/// What an import bound to a game version did (spec 0001): how many rows the diff added, how many
/// had their English changed, how many were invalidated (source-changed rows that already carried
/// Polish), how many were soft-removed and how many stayed the same.
/// It also reports how many rows were our own Polish coming back from a patched DAT (spec 0012).
/// Those are already counted in <paramref name="Unchanged"/>, or among the restored rows when they
/// were soft-removed, and they are listed only so the admin can see them.
/// Last come the notices that are not errors, such as rows that were re-added and restored.
/// </summary>
public sealed record ImportSummary(
    int Added,
    int SourceChanged,
    int Invalidated,
    int Removed,
    int Unchanged,
    int Echoed,
    IReadOnlyCollection<string> Warnings);
