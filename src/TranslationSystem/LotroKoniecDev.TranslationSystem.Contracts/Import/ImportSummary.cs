namespace LotroKoniecDev.TranslationSystem.Contracts.Import;

/// <summary>
/// The outcome of a version-bound import (spec 0001): how many rows the diff added, had their
/// English source changed, were invalidated (source-changed rows that carried Polish), soft-removed
/// or left untouched, how many rows were echoes of our own Polish coming back from a patched DAT
/// (spec 0012 — already counted in <paramref name="Unchanged"/>, or among the restored rows when
/// they were soft-removed; reported for observability), plus any non-fatal notices (e.g. restored
/// re-added rows).
/// </summary>
public sealed record ImportSummary(
    int Added,
    int SourceChanged,
    int Invalidated,
    int Removed,
    int Unchanged,
    int Echoed,
    IReadOnlyCollection<string> Warnings);
