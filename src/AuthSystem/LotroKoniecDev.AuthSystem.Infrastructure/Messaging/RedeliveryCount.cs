namespace LotroKoniecDev.AuthSystem.Infrastructure.Messaging;

/// <summary>
/// Reads the <c>x-delivery-count</c> header a quorum queue puts on every retry: missing on the first
/// delivery, then 1, 2 and so on. The broker compares it with <c>x-delivery-limit</c>, so the consumer
/// reads it instead of keeping its own count that a restart would reset.
/// </summary>
public static class RedeliveryCount
{
    private const string DeliveryCountHeader = "x-delivery-count";

    /// <summary>
    /// Returns 0 when the header is missing or unreadable. Counting too low is the safe side: it can only
    /// give a message more chances, never end it early.
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
