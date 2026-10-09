namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration;

/// <summary>
/// How many of the next erasures fail before they reach the database, the way an outage would make
/// them fail.
/// </summary>
#pragma warning disable CA1515
public sealed class ErasureFailures
#pragma warning restore CA1515
{
    private int _remaining;

    public int Remaining => Volatile.Read(ref _remaining);

    public void FailNext(int count) => Volatile.Write(ref _remaining, count);

    public bool TryTakeOne()
    {
        while (true)
        {
            int remaining = Volatile.Read(ref _remaining);
            if (remaining <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _remaining, remaining - 1, remaining) == remaining)
            {
                return true;
            }
        }
    }
}
