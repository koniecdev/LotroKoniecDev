using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.TranslationSystem.Projections;

/// <summary>
/// Write-side persistence port for the <see cref="PrecomputedTranslationFile"/> projection.
/// Deliberately not an <c>IRepository</c> (those are constrained to aggregate roots): the precomputed
/// file is a derived read projection, upserted by its natural key — one row per language. See ADR-0003.
/// </summary>
public interface IPrecomputedTranslationFileStore
{
    /// <summary>The projection is upserted by its natural key — one row per language.</summary>
    Task<Maybe<PrecomputedTranslationFile>> GetByLanguageAsync(string language, CancellationToken cancellationToken);

    void Insert(PrecomputedTranslationFile file);
}
