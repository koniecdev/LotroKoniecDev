using System.Collections.Concurrent;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

/// <summary>
/// Replaces the RabbitMQ publisher so the suite needs no broker: captures what the relay
/// publishes and, when <see cref="FailWith"/> is set, refuses every publish the way a dead
/// broker would.
/// </summary>
internal sealed class SpyMessagePublisher : IMessagePublisher
{
    internal sealed record PublishedMessage(string RoutingKey, string Payload, Guid MessageId);

    private readonly ConcurrentQueue<PublishedMessage> _published = new();

    public IReadOnlyCollection<PublishedMessage> Published => _published;

    public Exception? FailWith { get; set; }

    public Task PublishAsync(
        string routingKey,
        string payload,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        Exception? failure = FailWith;
        if (failure is not null)
        {
            throw failure;
        }

        _published.Enqueue(new PublishedMessage(routingKey, payload, messageId));
        return Task.CompletedTask;
    }

    public void Reset()
    {
        FailWith = null;
        _published.Clear();
    }
}
