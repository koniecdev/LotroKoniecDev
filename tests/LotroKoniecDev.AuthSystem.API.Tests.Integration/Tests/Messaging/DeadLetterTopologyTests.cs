using System.Collections;
using System.Diagnostics;
using System.Text;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Messaging;

/// <summary>
/// Proves what <see cref="RabbitMqTopologyDeclaration"/> guarantees, against a real broker: a rejected
/// message ends up in the dead-letter queue instead of disappearing, a message that uses up
/// <see cref="RabbitMqTopology.EmailDeliveryLimit"/> parks there instead of looping forever, and the
/// declaration can be run twice, so the publisher and the consumer may both run it.
/// These are things the broker does and not our code, so the assertions pin the queue arguments that buy
/// them (ADR-0036).
/// </summary>
public sealed class DeadLetterTopologyTests : IClassFixture<RabbitMqBrokerFixture>, IAsyncLifetime
{
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan EmptyQueueGrace = TimeSpan.FromSeconds(2);

    private readonly RabbitMqBrokerFixture _broker;
    private IConnection? _connection;
    private IChannel? _channel;

    public DeadLetterTopologyTests(RabbitMqBrokerFixture broker)
    {
        _broker = broker;
    }

    private IChannel Channel => _channel ?? throw new InvalidOperationException("Fixture not initialized.");

    public async Task InitializeAsync()
    {
        _connection = await _broker.ConnectAsync(CancellationToken.None);
        _channel = await _connection.CreateChannelAsync();
        await RabbitMqTopologyDeclaration.DeclareAsync(_channel, CancellationToken.None);
        await _channel.QueuePurgeAsync(RabbitMqTopology.EmailQueue);
        await _channel.QueuePurgeAsync(RabbitMqTopology.EmailDeadLetterQueue);
    }

    public async Task DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync();
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task Declaration_ShouldStayIdempotent_WhenDeclaredAgainOnAnotherChannel()
    {
        // Act: the publisher and the consumer both declare on channel open; a drifted argument
        // set would fail here with PRECONDITION_FAILED instead of being a no-op
        await using IChannel secondChannel = await _connection!.CreateChannelAsync();
        Task declareAgain = RabbitMqTopologyDeclaration.DeclareAsync(secondChannel, CancellationToken.None);

        // Assert
        await Should.NotThrowAsync(declareAgain);
    }

    [Fact]
    public async Task Broker_ShouldParkMessageInDeadLetterQueue_WhenConsumerRejectsIt()
    {
        // Arrange
        byte[] body = Encoding.UTF8.GetBytes("""{"poison":true}""");
        await PublishAsync(body);
        BasicGetResult delivery = (await GetWithinTimeoutAsync(RabbitMqTopology.EmailQueue, DeliveryTimeout)).ShouldNotBeNull();

        // Act: the consumer's poison path: reject without requeue
        await Channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false);

        // Assert: the message parked instead of vanishing, keeping its routing key for replay
        BasicGetResult dead = (await GetWithinTimeoutAsync(RabbitMqTopology.EmailDeadLetterQueue, DeliveryTimeout)).ShouldNotBeNull();
        dead.Body.ToArray().ShouldBe(body);
        dead.RoutingKey.ShouldBe(RabbitMqTopology.EmailConfirmationRoutingKey);
        FirstDeathReason(dead.BasicProperties).ShouldBe("rejected");
    }

    [Fact]
    public async Task Broker_ShouldParkMessageInDeadLetterQueue_WhenDeliveryLimitIsExhausted()
    {
        // Arrange
        byte[] body = Encoding.UTF8.GetBytes("""{"transient":true}""");
        await PublishAsync(body);

        // Act: run the consumer's retry path until it runs out, by rejecting and requeueing every
        // delivery. It uses basic.reject and a push consumer, like production, because only a reject, or
        // a lost connection, raises the x-delivery-count the limit is measured against. A nack with
        // requeue, or a BasicGet loop, would spin forever without ever reaching it (RabbitMQ 4.3 and
        // later).
        List<int> redeliveryCounts = [];
        await using IChannel consumerChannel = await _connection!.CreateChannelAsync();
        AsyncEventingBasicConsumer consumer = new(consumerChannel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            lock (redeliveryCounts)
            {
                redeliveryCounts.Add(RedeliveryCount.Read(delivery.BasicProperties.Headers));
            }

            await consumerChannel.BasicRejectAsync(delivery.DeliveryTag, requeue: true);
        };

        await consumerChannel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false);
        string consumerTag = await consumerChannel.BasicConsumeAsync(
            queue: RabbitMqTopology.EmailQueue,
            autoAck: false,
            consumer: consumer);

        // Assert: 1 initial delivery + the allowed redeliveries, then the parking lot
        BasicGetResult? dead = await GetWithinTimeoutAsync(RabbitMqTopology.EmailDeadLetterQueue, DeliveryTimeout);
        await consumerChannel.BasicCancelAsync(consumerTag);

        int[] observed;
        lock (redeliveryCounts)
        {
            observed = redeliveryCounts.ToArray();
        }

        dead.ShouldNotBeNull($"after {observed.Length} deliveries nothing was dead-lettered");
        // The exact sequence from 0 to the limit also pins RedeliveryCount.Read against the .NET type
        // the real client uses for x-delivery-count. If that broke, every entry would read 0 and the
        // consumer would never reach its give-up branch in production.
        observed.ShouldBe(Enumerable.Range(0, RabbitMqTopology.EmailDeliveryLimit + 1).ToArray());
        dead.Body.ToArray().ShouldBe(body);
        FirstDeathReason(dead.BasicProperties).ShouldBe("delivery_limit");
    }

    [Fact]
    public async Task Broker_ShouldLeaveDeadLetterQueueEmpty_WhenDeliveryIsAcked()
    {
        // Arrange
        await PublishAsync(Encoding.UTF8.GetBytes("""{"happy":true}"""));
        BasicGetResult delivery = (await GetWithinTimeoutAsync(RabbitMqTopology.EmailQueue, DeliveryTimeout)).ShouldNotBeNull();

        // Act
        await Channel.BasicAckAsync(delivery.DeliveryTag, multiple: false);

        // Assert
        (await GetWithinTimeoutAsync(RabbitMqTopology.EmailQueue, EmptyQueueGrace)).ShouldBeNull();
        (await GetWithinTimeoutAsync(RabbitMqTopology.EmailDeadLetterQueue, EmptyQueueGrace)).ShouldBeNull();
    }

    private async Task PublishAsync(byte[] body)
    {
        BasicProperties properties = new()
        {
            MessageId = Guid.NewGuid().ToString(),
            DeliveryMode = DeliveryModes.Persistent
        };

        await Channel.BasicPublishAsync(
            exchange: RabbitMqTopology.EmailsExchange,
            routingKey: RabbitMqTopology.EmailConfirmationRoutingKey,
            mandatory: true,
            basicProperties: properties,
            body: body);
    }

    /// <summary>
    /// Polls the queue until a delivery arrives or the timeout passes: dead-lettering and
    /// requeueing are asynchronous inside the broker, so a single immediate get would race them.
    /// </summary>
    private async Task<BasicGetResult?> GetWithinTimeoutAsync(string queue, TimeSpan timeout)
    {
        Stopwatch elapsed = Stopwatch.StartNew();

        while (true)
        {
            BasicGetResult? delivery = await Channel.BasicGetAsync(queue, autoAck: false);
            if (delivery is not null)
            {
                return delivery;
            }

            if (elapsed.Elapsed >= timeout)
            {
                return null;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }

    /// <summary>
    /// Reads the reason of the first (most recent) <c>x-death</c> entry the broker stamped on a
    /// dead-lettered message.
    /// </summary>
    private static string? FirstDeathReason(IReadOnlyBasicProperties properties)
    {
        if (properties.Headers is not { } headers
            || !headers.TryGetValue("x-death", out object? deathsRaw)
            || deathsRaw is not IList { Count: > 0 } deaths
            || deaths[0] is not IDictionary firstDeath
            || !firstDeath.Contains("reason"))
        {
            return null;
        }

        return firstDeath["reason"] is byte[] reason ? Encoding.UTF8.GetString(reason) : null;
    }
}
