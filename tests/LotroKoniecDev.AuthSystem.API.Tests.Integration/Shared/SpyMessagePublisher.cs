using System.Collections.Concurrent;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

/// <summary>
/// Replaces the RabbitMQ publisher so the suite needs no broker: captures what the relay
/// publishes and, when <see cref="FailWith"/> is set, refuses every publish the way a dead
/// broker would. The optional delivery bridge stands in for the broker-to-consumer hop:
/// each accepted publish is handed straight to it, the way the real queue would push the
/// message at <c>EmailDispatchConsumer</c> (which the suite removes, having no broker).
/// </summary>
internal sealed class SpyMessagePublisher : IMessagePublisher
{
    internal sealed record PublishedMessage(string Exchange, string RoutingKey, string Type, string Payload, Guid MessageId);

    private readonly ConcurrentQueue<PublishedMessage> _published = new();
    private readonly Func<PublishedMessage, Task>? _deliverAsync;

    public SpyMessagePublisher(Func<PublishedMessage, Task>? deliverAsync = null)
    {
        _deliverAsync = deliverAsync;
    }

    public IReadOnlyCollection<PublishedMessage> Published => _published;

    public Exception? FailWith { get; set; }

    /// <summary>
    /// Refuses only the publishes it returns an exception for, the way a broker refuses a message whose
    /// routing key no queue is bound to yet.
    /// </summary>
    public Func<PublishedMessage, Exception?>? FailWhen { get; set; }

    public async Task PublishAsync(
        string exchange,
        string routingKey,
        string type,
        string payload,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        Exception? failure = FailWith;
        if (failure is not null)
        {
            throw failure;
        }

        PublishedMessage message = new(exchange, routingKey, type, payload, messageId);
        if (FailWhen?.Invoke(message) is { } refusal)
        {
            throw refusal;
        }

        _published.Enqueue(message);

        if (_deliverAsync is not null)
        {
            await _deliverAsync(message);
        }
    }

    public void Reset()
    {
        FailWith = null;
        FailWhen = null;
        _published.Clear();
    }
}
