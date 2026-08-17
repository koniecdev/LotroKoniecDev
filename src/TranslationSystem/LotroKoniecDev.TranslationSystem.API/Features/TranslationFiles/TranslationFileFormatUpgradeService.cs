using LotroKoniecDev.TranslationSystem.API.Parsing;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;
using Microsoft.EntityFrameworkCore;

namespace LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

/// <summary>
/// Regenerates a stored artifact once at startup when it predates the <c>source_digest</c> column
/// (ADR-0047, Consequences — "Deploy ordering"). The artifact is otherwise rebuilt only on the next
/// approve/import signal, so without this an upgraded CLI would download a six-column file and patch
/// nothing — reporting it, and launching the game — until someone happened to approve something.
/// </summary>
/// <remarks>
/// Runs off the startup path deliberately: it must never keep the API from becoming ready, and a
/// database that is still waking up (a serverless Postgres resume outlasting the connect timeout)
/// must degrade to a logged failure, not a dead host. The next write's rebuild converges anyway.
/// Idempotent by construction — once the artifact carries the column, later starts do nothing.
/// </remarks>
internal sealed partial class TranslationFileFormatUpgradeService : BackgroundService
{
    /// <summary>
    /// How much of the stored artifact to read to judge its format. Only the first data row matters,
    /// and the column lives at its end, so a bounded prefix keeps a multi-MB TOASTed column out of
    /// the startup path entirely.
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
            // Swallowed deliberately: an exception escaping ExecuteAsync stops the whole host, and a
            // stale artifact is a degraded patch, not a dead API. The next approve/import rebuilds it.
            LogRegenerationFailed(_logger, exception);
        }
    }

    /// <summary>
    /// The upgrade itself, exposed so an integration suite can drive it against real PostgreSQL.
    /// Worth that seam: the format probe reads a bounded PREFIX of the artifact so the multi-MB
    /// TOASTed column never lands on the startup path, and whether that projection translates to SQL
    /// is not something a fake DbContext can answer — while the catch above would turn a failure to
    /// translate it into a log line nobody reads and a feature that silently never fires.
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

/// <summary>The bounded prefix of one language's stored artifact, enough to read its first data row.</summary>
internal sealed record ArtifactFormatProbe(string Language, string ContentPrefix);

/// <summary>
/// Reads a stored artifact's format off its own content — there is no header comment to stamp
/// (the serializer emits rows only), so the format IS whether the first data row carries the
/// seventh column.
/// </summary>
internal static class ArtifactFormatStamp
{
    private const string LineTerminator = "\r\n";

    /// <summary>
    /// Whether the artifact was written before ADR-0047. Judged on the first COMPLETE data row only:
    /// a line truncated by the caller's prefix cannot be carved, and answering
    /// <see langword="true"/> for it would regenerate a perfectly current artifact on every start.
    /// An empty artifact carries no format at all, so there is nothing to upgrade either.
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
