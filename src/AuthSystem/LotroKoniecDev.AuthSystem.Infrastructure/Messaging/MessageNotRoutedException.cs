namespace LotroKoniecDev.AuthSystem.Infrastructure.Messaging;

/// <summary>
/// The broker took the message but no queue is bound to its routing key, so it came back. Unlike a
/// broker that is down, this is about one message: the next one, to another key, can still go
/// (ADR-0065).
/// </summary>
public sealed class MessageNotRoutedException : Exception
{
    public MessageNotRoutedException(string exchange, string routingKey, Exception? innerException = null)
        : base($"No queue is bound to routing key '{routingKey}' on exchange '{exchange}'.", innerException)
    {
        Exchange = exchange;
        RoutingKey = routingKey;
    }

    public string Exchange { get; }

    public string RoutingKey { get; }
}
