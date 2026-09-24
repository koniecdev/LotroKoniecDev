namespace LotroKoniecDev.AuthSystem.API.Services.ResponseTiming;

/// <summary>
/// One running clock from <see cref="IResponseTimeFloor"/>.
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
    /// Returns once the floor has passed since the clock started, at once if it already has. The wait is a
    /// timer, so a held answer costs no thread and no CPU.
    /// </summary>
    public async Task WaitForFloorAsync(CancellationToken cancellationToken)
    {
        TimeSpan remaining = Remaining();
        while (remaining > TimeSpan.Zero)
        {
            // A timer can fire a little early (up to about 15 ms on Windows), and the floor must be a lower
            // bound, so the time is checked again after every delay. The system timer drops the part below
            // one millisecond, so the delay is rounded up to avoid a spin of zero-length delays at the end.
            TimeSpan delay = TimeSpan.FromMilliseconds(Math.Ceiling(remaining.TotalMilliseconds));
            await Task.Delay(delay, _timeProvider, cancellationToken);
            remaining = Remaining();
        }
    }

    private TimeSpan Remaining() => _floor - _timeProvider.GetElapsedTime(_startedAt);
}
