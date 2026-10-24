using System.Threading.Channels;

namespace LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

/// <summary>
/// An unbounded channel, so <see cref="Schedule"/> never blocks or fails on a full queue (PERF-04). It
/// lives in the process, which assumes a single API instance; ADR-0021 says what would change that.
/// </summary>
internal sealed class TranslationFileRebuildScheduler : ITranslationFileRebuildScheduler
{
    private readonly Channel<string> _signals = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });

    private int _pendingCount;

    /// <summary>Only the worker may read it.</summary>
    public ChannelReader<string> Reader => _signals.Reader;

    /// <summary>
    /// Zero means every scheduled rebuild has finished, which the integration tests wait for before
    /// they truncate the tables.
    /// </summary>
    public int PendingCount => Volatile.Read(ref _pendingCount);

    public void Schedule(string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);

        Interlocked.Increment(ref _pendingCount);
        _signals.Writer.TryWrite(language);
    }

    public void MarkCompleted(int consumedSignalCount) => Interlocked.Add(ref _pendingCount, -consumedSignalCount);
}
