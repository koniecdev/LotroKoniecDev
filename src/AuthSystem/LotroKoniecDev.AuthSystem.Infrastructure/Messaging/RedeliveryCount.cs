namespace LotroKoniecDev.AuthSystem.Infrastructure.Messaging;

/// <summary>
/// Reads the <c>x-delivery-count</c> header a quorum queue stamps on every redelivery: absent on
/// the first delivery, then 1, 2, … — the very counter the broker compares against
/// <c>x-delivery-limit</c> when it decides to dead-letter. Reading it lets the consumer scale its
/// backoff per attempt and announce the final attempt, instead of keeping a count of its own that
/// a restart would reset.
/// </summary>
public static class RedeliveryCount
{
    private const string DeliveryCountHeader = "x-delivery-count";

    /// <summary>
    /// Returns the broker's redelivery count, or 0 when the header is absent or unreadable —
    /// under-counting is the safe direction, because it can only grant a message extra patience,
    /// never park it early.
    /// </summary>
    public static int Read(IDictionary<string, object?>? headers)
    {
        if (headers is null || !headers.TryGetValue(DeliveryCountHeader, out object? value))
        {
            return 0;
        }

        return value switch
        {
            long count => (int)long.Clamp(count, 0L, int.MaxValue),
            int count => int.Max(count, 0),
            byte count => count,
            _ => 0
        };
    }
}
