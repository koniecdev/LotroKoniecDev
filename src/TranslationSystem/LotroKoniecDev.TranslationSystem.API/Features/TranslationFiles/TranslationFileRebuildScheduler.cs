using System.Threading.Channels;

namespace LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

/// <summary>
/// The queue of "needs rebuilding" signals between the write handlers and the rebuild worker (PERF-04,
/// ADR-0021). It is a singleton around a channel with no size limit, so <see cref="Schedule"/> never
/// blocks and never fails on a full queue, and the write path only pays for adding an item.
/// Keeping it inside the process assumes a single API instance on purpose. With several instances each
/// process would only rebuild after its own writes and miss the others'. ADR-0021 says what would make
/// us change that.
/// </summary>
internal sealed class TranslationFileRebuildScheduler : ITranslationFileRebuildScheduler
{
    private readonly Channel<string> _signals = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });

    private int _pendingCount;

    /// <summary>The worker's end of the queue. Nothing else may read it.</summary>
    public ChannelReader<string> Reader => _signals.Reader;

    /// <summary>
    /// How many signals are waiting for a rebuild. Zero means every scheduled rebuild has finished,
    /// which is what the integration tests wait for before they truncate the tables.
    /// </summary>
    public int PendingCount => Volatile.Read(ref _pendingCount);

    public void Schedule(string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);

        Interlocked.Increment(ref _pendingCount);
        _signals.Writer.TryWrite(language);
    }

    /// <summary>The worker calls this once the rebuild that covers those signals has finished.</summary>
    public void MarkCompleted(int consumedSignalCount) => Interlocked.Add(ref _pendingCount, -consumedSignalCount);
}
