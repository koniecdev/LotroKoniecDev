using LotroKoniecDev.TranslationSystem.API.Parsing;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;
using Microsoft.EntityFrameworkCore;

namespace LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

/// <summary>
/// Rebuilds a stored artifact written before the <c>source_digest</c> column existed (ADR-0047, "Deploy
/// ordering" in its Consequences). Without it an updated CLI would patch nothing until someone happened
/// to approve something.
/// </summary>
/// <remarks>
/// Outside the startup path: it must never keep the API from becoming ready, and a database still
/// waking up must end in a log line, not a dead host. Once the artifact has the column, later starts do
/// nothing.
/// </remarks>
internal sealed partial class TranslationFileFormatUpgradeService : BackgroundService
{
    /// <summary>
    /// Only the first data row matters, so a small prefix keeps the multi-MB column out of the read.
    /// </summary>
    private const int InspectedPrefixLength = 8192;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPrecomputedTranslationFileProjector _projector;
    private readonly ILogger<TranslationFileFormatUpgradeService> _logger;

    public TranslationFileFormatUpgradeService(
        IServiceScopeFactory scopeFactory,
        IPrecomputedTranslationFileProjector projector,
        ILogger<TranslationFileFormatUpgradeService> logger)
    {
        _scopeFactory = scopeFactory;
        _projector = projector;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await UpgradeAsync(stoppingToken);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            // An exception leaving ExecuteAsync stops the host, and an old artifact only means a worse
            // patch. The next approve or import rebuilds it.
            LogRegenerationFailed(_logger, exception);
        }
    }

    /// <summary>
    /// Internal so an integration test can prove the prefix query translates to SQL. The catch above
    /// would otherwise hide that failure in a log line nobody reads.
    /// </summary>
    internal async Task UpgradeAsync(CancellationToken stoppingToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IApplicationReadDbContext readDbContext = scope.ServiceProvider.GetRequiredService<IApplicationReadDbContext>();

        List<ArtifactFormatProbe> probes = await readDbContext.PrecomputedTranslationFiles
            .Select(file => new ArtifactFormatProbe(file.Language, file.Content.Substring(0, InspectedPrefixLength)))
            .ToListAsync(stoppingToken);

        foreach (ArtifactFormatProbe probe in probes.Where(probe => ArtifactFormatStamp.PredatesSourceDigest(probe.ContentPrefix)))
        {
            LogRegenerating(_logger, probe.Language);
            await _projector.RebuildAsync(probe.Language, stoppingToken);
            LogRegenerated(_logger, probe.Language);
        }
    }

    [LoggerMessage(EventId = EventIds.TranslationFileFormatUpgradeStarted, Level = LogLevel.Information, Message = "Stored translation file for '{Language}' predates the source_digest column; regenerating")]
    private static partial void LogRegenerating(ILogger logger, string language);

    [LoggerMessage(EventId = EventIds.TranslationFileFormatUpgradeCompleted, Level = LogLevel.Information, Message = "Stored translation file for '{Language}' regenerated with the source_digest column")]
    private static partial void LogRegenerated(ILogger logger, string language);

    [LoggerMessage(EventId = EventIds.TranslationFileFormatUpgradeFailed, Level = LogLevel.Error, Message = "The startup regeneration of the stored translation file failed; the next approve or import will rebuild it")]
    private static partial void LogRegenerationFailed(ILogger logger, Exception exception);
}

internal sealed record ArtifactFormatProbe(string Language, string ContentPrefix);

/// <summary>
/// The serializer writes rows only, with no header, so the format is whether the first data row has the
/// seventh column.
/// </summary>
internal static class ArtifactFormatStamp
{
    private const string LineTerminator = "\r\n";

    /// <summary>
    /// Only a complete first row counts: a line the prefix cut in half would otherwise rebuild a current
    /// artifact on every start. An empty artifact has nothing to upgrade.
    /// </summary>
    public static bool PredatesSourceDigest(string contentPrefix)
    {
        ArgumentNullException.ThrowIfNull(contentPrefix);

        int lineEnd = contentPrefix.IndexOf(LineTerminator, StringComparison.Ordinal);
        if (lineEnd < 0)
        {
            return false;
        }

        return !TranslationLineCarver.TryCarve(contentPrefix[..lineEnd], out CarvedTranslationLine? carved)
               || carved.SourceDigest is null;
    }
}
