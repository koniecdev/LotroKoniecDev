namespace LotroKoniecDev.AuthSystem.API.Services.ResponseTiming;

internal sealed class ResponseTimeFloor : IResponseTimeFloor
{
    private readonly TimeProvider _timeProvider;

    public ResponseTimeFloor(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public async Task<T> HoldAsync<T>(TimeSpan floor, Func<Task<T>> work, Func<T, bool>? skipWaitFor = null)
    {
        ResponseTimer timer = new(_timeProvider, floor);

        T result;
        try
        {
            result = await work();
        }
        catch
        {
            await timer.WaitForFloorAsync();
            throw;
        }

        if (skipWaitFor is null || !skipWaitFor(result))
        {
            await timer.WaitForFloorAsync();
        }

        return result;
    }

    public async Task HoldAsync(TimeSpan floor, Func<Task> work) =>
        await HoldAsync(floor, async () =>
        {
            await work();
            return true;
        });
}
