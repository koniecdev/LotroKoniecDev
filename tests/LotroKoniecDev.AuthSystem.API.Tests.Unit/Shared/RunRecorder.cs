using System.Threading.Channels;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Shared;

/// <summary>
/// Counts the runs of a background job, as its stubbed dependency sees them, and lets the test wait
/// for the next one. The job may run on another thread, so the test cannot simply look after the call.
/// </summary>
internal sealed class RunRecorder
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    private readonly Channel<int> _runs = Channel.CreateUnbounded<int>();
    private int _count;

    public int Count => Volatile.Read(ref _count);

    /// <summary>
    /// Records one run and returns its number, starting at 1.
    /// </summary>
    public int Record()
    {
        int run = Interlocked.Increment(ref _count);
        _runs.Writer.TryWrite(run);
        return run;
    }

    /// <summary>
    /// Waits for the first run that has not been waited for yet.
    /// </summary>
    public async Task WaitForNextRunAsync()
    {
        try
        {
            await _runs.Reader.ReadAsync().AsTask().WaitAsync(WaitTimeout);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"The job did not run within {WaitTimeout.TotalSeconds} seconds. Runs so far: {Count}.", exception);
        }
    }
}
