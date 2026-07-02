using System.Threading.Channels;

namespace LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

/// <summary>
/// In-process dirty-signal queue between the write handlers and the rebuild worker (PERF-04,
/// ADR-0021). A singleton wrapping an unbounded channel: <see cref="Schedule"/> never blocks and
/// never throws on a full queue, so the hot path pays only an enqueue. In-process is a deliberate
/// single-replica assumption — with multiple API replicas each process would rebuild on its own
/// writes only, missing the others' (see ADR-0021 for the scale-out trigger).
/// </summary>
internal sealed class TranslationFileRebuildScheduler : ITranslationFileRebuildScheduler
{
    private readonly Channel<string> _signals = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });

    private int _pendingCount;

    /// <summary>The worker's end of the queue; nothing else may read it.</summary>
    public ChannelReader<string> Reader => _signals.Reader;

    /// <summary>
    /// Signals scheduled but not yet rebuilt. Zero means every scheduled rebuild has completed —
    /// the quiesce point the integration suite waits for before truncating tables.
    /// </summary>
    public int PendingCount => Volatile.Read(ref _pendingCount);

    public void Schedule(string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);

        Interlocked.Increment(ref _pendingCount);
        _signals.Writer.TryWrite(language);
    }

    /// <summary>Called by the worker once the rebuild covering the drained signals has finished.</summary>
    public void MarkCompleted(int consumedSignalCount) => Interlocked.Add(ref _pendingCount, -consumedSignalCount);
}
