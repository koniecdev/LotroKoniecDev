namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration;

/// <summary>
/// How many of the next erasures fail before they reach the database, and with what: a
/// <see cref="TimeoutException"/> reads as an outage, anything else as a failure that comes back every
/// time.
/// </summary>
#pragma warning disable CA1515
public sealed class ErasureFailures
#pragma warning restore CA1515
{
    private readonly Lock _lock = new();
    private int _remaining;
    private Func<Exception> _createFailure = () => new TimeoutException("simulated database outage");

    public int Remaining
    {
        get
        {
            lock (_lock)
            {
                return _remaining;
            }
        }
    }

    public void FailNext(int count, Func<Exception>? createFailure = null)
    {
        lock (_lock)
        {
            _remaining = count;
            _createFailure = createFailure ?? (() => new TimeoutException("simulated database outage"));
        }
    }

    public Exception? TakeOne()
    {
        lock (_lock)
        {
            if (_remaining <= 0)
            {
                return null;
            }

            _remaining--;
            return _createFailure();
        }
    }
}
