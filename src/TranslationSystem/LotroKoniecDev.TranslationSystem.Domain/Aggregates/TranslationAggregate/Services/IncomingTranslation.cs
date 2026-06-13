using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;

/// <summary>
/// One parsed-and-validated row of an uploaded export, ready for the diff: its identity and its
/// English source. The API parser produces the raw rows; the import handler validates them into
/// these value-object pairs before handing them to <see cref="TranslationDiffService"/>.
/// </summary>
public sealed record IncomingTranslation(FragmentKey Key, TranslationSource Source);
