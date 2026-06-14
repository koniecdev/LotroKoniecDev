using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.TranslationSystem.API.Features.Bootstrap;

/// <summary>
/// Merges the existing production <c>polish.txt</c> onto already-imported baseline rows as Approved
/// (#28). Merge-only and idempotent; never creates rows of its own.
/// </summary>
internal interface IPolishTranslationSeeder
{
    Task<Result<PolishSeedSummary>> SeedAsync(Stream polishStream, CancellationToken cancellationToken);
}
