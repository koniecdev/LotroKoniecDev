using LotroKoniecDev.TranslationSystem.Contracts.Import;

namespace LotroKoniecDev.TranslationSystem.API.Features.Bootstrap;

/// <summary>
/// What the bootstrap actually did this run: the baseline import summary (null when the baseline was
/// skipped — not configured or already present) and the Polish-seed summary (null when no seed file
/// was found). Returned so the startup hook can log it and tests can assert it.
/// </summary>
internal sealed record BootstrapReport(ImportSummary? Baseline, PolishSeedSummary? Polish);
