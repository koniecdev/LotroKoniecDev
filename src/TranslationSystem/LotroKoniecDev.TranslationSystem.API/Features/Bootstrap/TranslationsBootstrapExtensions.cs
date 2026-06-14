using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Common;
using LotroKoniecDev.TranslationSystem.API.Features.GameVersions;
using LotroKoniecDev.TranslationSystem.API.Features.Import;
using LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Contracts.Import;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.TranslationSystem.API.Features.Bootstrap;

/// <summary>
/// Startup bootstrap (spec 0001, first run / #28), mirroring the AuthSystem seed pattern: ensure the
/// schema, optionally import the English baseline for the initial <c>GameVersion</c>, then merge the
/// production <c>polish.txt</c> onto those rows as Approved. Opt-in and idempotent — disabled by
/// default and safe to leave on across restarts.
/// </summary>
internal static class TranslationsBootstrapExtensions
{
    private const string LoggerCategory = "TranslationsBootstrap";

    public static async Task BootstrapTranslationsAsync(this WebApplication app)
    {
        BootstrapSettings settings = app.Services.GetRequiredService<IOptions<BootstrapSettings>>().Value;

        using IServiceScope scope = app.Services.CreateScope();
        await BootstrapTranslationsAsync(scope.ServiceProvider, settings, CancellationToken.None);
    }

    internal static async Task<BootstrapReport> BootstrapTranslationsAsync(
        IServiceProvider services,
        BootstrapSettings settings,
        CancellationToken cancellationToken)
    {
        ILogger logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);

        if (!settings.Enabled)
        {
            logger.LogInformation("Translations bootstrap disabled (Bootstrap:Enabled=false); skipping.");
            return new BootstrapReport(null, null);
        }

        // The schema is owned by the compose migrator (CLAUDE.md) — the bootstrap only seeds data and
        // assumes the DB is already migrated, so it must run after the migrator / `dotnet ef`.
        ImportSummary? baseline = await ImportBaselineIfNeededAsync(services, settings, logger, cancellationToken);
        PolishSeedSummary? polish = await SeedPolishIfPresentAsync(services, settings, logger, cancellationToken);

        // The seed approves rows, so the pre-built distribution artifact must be regenerated to
        // include them (spec 0001: regenerate on write; the download endpoint never builds per-request).
        if (polish is { Approved: > 0 })
        {
            IPrecomputedTranslationFileProjector projector = services.GetRequiredService<IPrecomputedTranslationFileProjector>();
            await projector.RebuildAsync(SupportedLanguages.Polish, cancellationToken);
        }

        return new BootstrapReport(baseline, polish);
    }

    private static async Task<ImportSummary?> ImportBaselineIfNeededAsync(
        IServiceProvider services,
        BootstrapSettings settings,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.GameVersion) || string.IsNullOrWhiteSpace(settings.ExportedTextPath))
        {
            logger.LogInformation(
                "Baseline import skipped: GameVersion/ExportedTextPath not configured "
                + "(assuming the baseline was already imported via the import endpoint).");
            return null;
        }

        Result<LotroNotationVersion> versionResult = LotroNotationVersion.Create(settings.GameVersion);
        if (versionResult.IsFailure)
        {
            logger.LogWarning(
                "Baseline import skipped: invalid GameVersion '{Version}' ({Error}).",
                settings.GameVersion, versionResult.Error.Message);
            return null;
        }

        IGameVersionRepository gameVersionRepository = services.GetRequiredService<IGameVersionRepository>();
        if (await gameVersionRepository.ExistsByVersionAsync(versionResult.Value, cancellationToken))
        {
            logger.LogInformation(
                "Baseline import skipped: game version '{Version}' already exists (DB already bootstrapped).",
                settings.GameVersion);
            return null;
        }

        if (!File.Exists(settings.ExportedTextPath))
        {
            logger.LogWarning("Baseline import skipped: exported file not found at '{Path}'.", settings.ExportedTextPath);
            return null;
        }

        ICommandHandler<RegisterGameVersion.Command, Result<GameVersionResponse>> registerHandler =
            services.GetRequiredService<ICommandHandler<RegisterGameVersion.Command, Result<GameVersionResponse>>>();
        Result<GameVersionResponse> registerResult =
            await registerHandler.Handle(new RegisterGameVersion.Command(settings.GameVersion), cancellationToken);
        if (registerResult.IsFailure)
        {
            logger.LogWarning("Baseline import skipped: could not register game version ({Error}).", registerResult.Error.Message);
            return null;
        }

        GameVersionId versionId = registerResult.Value.Id;

        await using FileStream exportedStream = File.OpenRead(settings.ExportedTextPath);
        ICommandHandler<ImportExportedTexts.Command, Result<ImportSummary>> importHandler =
            services.GetRequiredService<ICommandHandler<ImportExportedTexts.Command, Result<ImportSummary>>>();
        Result<ImportSummary> importResult =
            await importHandler.Handle(new ImportExportedTexts.Command(versionId, exportedStream, false), cancellationToken);
        if (importResult.IsFailure)
        {
            logger.LogError("Baseline import failed: {Error}.", importResult.Error.Message);
            return null;
        }

        logger.LogInformation(
            "Baseline import complete: {Added} row(s) added for version {Version}.",
            importResult.Value.Added, settings.GameVersion);
        return importResult.Value;
    }

    private static async Task<PolishSeedSummary?> SeedPolishIfPresentAsync(
        IServiceProvider services,
        BootstrapSettings settings,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.PolishTextPath) || !File.Exists(settings.PolishTextPath))
        {
            logger.LogWarning("Polish seed skipped: file not found at '{Path}'.", settings.PolishTextPath);
            return null;
        }

        IPolishTranslationSeeder seeder = services.GetRequiredService<IPolishTranslationSeeder>();

        await using FileStream polishStream = File.OpenRead(settings.PolishTextPath);
        Result<PolishSeedSummary> seedResult = await seeder.SeedAsync(polishStream, cancellationToken);
        if (seedResult.IsFailure)
        {
            logger.LogError("Polish seed failed: {Error}.", seedResult.Error.Message);
            return null;
        }

        return seedResult.Value;
    }
}
