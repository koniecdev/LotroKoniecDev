namespace LotroKoniecDev.AuthSystem.API.Services.ResponseTiming;

/// <summary>
/// One running clock for <see cref="ResponseTimeFloor"/>.
/// </summary>
internal sealed class ResponseTimer
{
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _floor;
    private readonly long _startedAt;

    public ResponseTimer(TimeProvider timeProvider, TimeSpan floor)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(floor, TimeSpan.Zero);

        _timeProvider = timeProvider;
        _floor = floor;
        _startedAt = timeProvider.GetTimestamp();
    }

    /// <summary>
    /// The wait is a timer, so a held answer costs no thread. No cancellation token: a cancelled wait would
    /// replace the real outcome, a committed success or the work's own exception, with a cancellation.
    /// </summary>
    public async Task WaitForFloorAsync()
    {
        TimeSpan remaining = Remaining();
        while (remaining > TimeSpan.Zero)
        {
            // A timer can fire early (about 15 ms on Windows), so the time is checked again after every
            // delay. Rounded up, because the timer drops the part below one millisecond and would spin.
            TimeSpan delay = TimeSpan.FromMilliseconds(Math.Ceiling(remaining.TotalMilliseconds));
            await Task.Delay(delay, _timeProvider);
            remaining = Remaining();
        }
    }

    private TimeSpan Remaining() => _floor - _timeProvider.GetElapsedTime(_startedAt);
}
