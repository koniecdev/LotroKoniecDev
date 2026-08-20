using LotroKoniecDev.TranslationSystem.API.Parsing;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;
using Microsoft.EntityFrameworkCore;

namespace LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

/// <summary>
/// Rebuilds a stored artifact once at startup when it was written before the <c>source_digest</c>
/// column existed (ADR-0047, "Deploy ordering" in its Consequences). Otherwise the artifact is only
/// rebuilt on the next approve or import, so without this an updated CLI would download a six-column
/// file, patch nothing, report that, and launch the game, until someone happened to approve something.
/// </summary>
/// <remarks>
/// It runs outside the startup path on purpose. It must never keep the API from becoming ready, and a
/// database that is still waking up, for example a serverless Postgres taking longer to resume than
/// the connect timeout allows, must end in a logged failure and not a dead host. The next write
/// rebuilds the artifact anyway.
/// Running it twice is harmless: once the artifact has the column, later starts do nothing.
/// </remarks>
internal sealed partial class TranslationFileFormatUpgradeService : BackgroundService
{
    /// <summary>
    /// How much of the stored artifact to read to tell its format. Only the first data row matters, and
    /// the column sits at its end, so reading a small prefix keeps the multi-MB column out of the
    /// startup path.
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
            // Ignored on purpose. An exception leaving ExecuteAsync stops the whole host, and an
            // out-of-date artifact only means a worse patch, not a dead API. The next approve or import
            // rebuilds it.
            LogRegenerationFailed(_logger, exception);
        }
    }

    /// <summary>
    /// The upgrade itself. It is exposed so an integration test can run it against a real PostgreSQL.
    /// That is worth it: the format check reads only a prefix of the artifact, so the multi-MB column
    /// never reaches the startup path, and only a real database can say whether that query translates
    /// to SQL. The catch above would otherwise turn a failure to translate it into a log line nobody
    /// reads and a feature that never runs.
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

/// <summary>The first part of one language's stored artifact, enough to read its first data row.</summary>
internal sealed record ArtifactFormatProbe(string Language, string ContentPrefix);

/// <summary>
/// Works out a stored artifact's format from its own content. There is no header to stamp, because the
/// serializer writes rows only, so the format is simply whether the first data row has the seventh
/// column.
/// </summary>
internal static class ArtifactFormatStamp
{
    private const string LineTerminator = "\r\n";

    /// <summary>
    /// Whether the artifact was written before ADR-0047. It looks only at the first complete data row:
    /// a line the prefix cut in half cannot be parsed, and answering <see langword="true"/> for it would
    /// rebuild a perfectly current artifact on every start. An empty artifact has no format at all, so
    /// there is nothing to upgrade there either.
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
