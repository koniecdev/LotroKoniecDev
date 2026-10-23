using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;

namespace LotroKoniecDev.Tests.Shared;

/// <summary>
/// A fake clock that tells the test when the code under test starts a timer, and how often its timers
/// have gone off (#948). A background job starts its timer on its own thread, so a test that moves the
/// clock before the timer exists moves it for nothing. It counts every timer on the clock, so it
/// expects the job's only timers to be its schedule. Linked into the auth API and TMS API unit suites
/// (#1223).
/// </summary>
internal sealed class TimerWatchingTimeProvider : FakeTimeProvider
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    private readonly Channel<bool> _startedTimers = Channel.CreateUnbounded<bool>();
    private int _wakeUps;

    /// <summary>
    /// How many times a timer of this clock has gone off. <see cref="FakeTimeProvider.Advance"/> fires
    /// the due timers on the test's own thread, unless another thread is starting a timer at that
    /// moment. A test that moves the clock only after <see cref="WaitForNextTimerAsync"/> meets that
    /// rule, so the count is exact as soon as <c>Advance</c> returns. The work a timer wakes up may run
    /// later on another thread, so a count of that work could still miss an early wake-up.
    /// </summary>
    public int WakeUps => Volatile.Read(ref _wakeUps);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ITimer timer = base.CreateTimer(
            timerState =>
            {
                Interlocked.Increment(ref _wakeUps);
                callback(timerState);
            },
            state,
            dueTime,
            period);
        _startedTimers.Writer.TryWrite(true);
        return timer;
    }

    /// <summary>
    /// Waits until the code under test has started a timer the test has not waited for yet. That timer
    /// counts from the fake time at which it started.
    /// </summary>
    public async Task WaitForNextTimerAsync()
    {
        try
        {
            await _startedTimers.Reader.ReadAsync().AsTask().WaitAsync(WaitTimeout);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"No new timer was started within {WaitTimeout.TotalSeconds} seconds.", exception);
        }
    }
}
