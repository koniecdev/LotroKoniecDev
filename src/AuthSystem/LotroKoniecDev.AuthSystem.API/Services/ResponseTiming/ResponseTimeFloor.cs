namespace LotroKoniecDev.AuthSystem.API.Services.ResponseTiming;

internal sealed class ResponseTimeFloor : IResponseTimeFloor
{
    private readonly TimeProvider _timeProvider;

    public ResponseTimeFloor(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public ResponseTimer Start(TimeSpan floor) => new(_timeProvider, floor);
}
