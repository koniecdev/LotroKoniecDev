using System.Text;
using LotroKoniecDev.SharedKernel.IntegrationEvents;
using LotroKoniecDev.Tests.Shared;
using LotroKoniecDev.TranslationSystem.API.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Testcontainers.RabbitMq;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Messaging;

/// <summary>
/// Pins the broker behaviour the account event consumer is built on (ADR-0065), against the broker
/// version the stacks run. A message the database refused goes back with <c>basic.nack</c>, which must
/// never park it, however long the outage lasts. A message that keeps breaking the consumer goes back
/// with <c>basic.reject</c>, which must park it at the delivery limit instead of looping.
/// No API host runs here, so no consumer takes the deliveries away from the test.
/// </summary>
public sealed class AccountEventsTopologyTests : IAsyncLifetime
{
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EmptyQueueGrace = TimeSpan.FromSeconds(2);

    private readonly RabbitMqContainer _broker = new RabbitMqBuilder(RabbitMqImage.Name).Build();
    private IConnection? _connection;
    private IChannel? _channel;

    private IChannel Channel => _channel ?? throw new InvalidOperationException("The channel is not open.");

    public async Task InitializeAsync()
    {
        await _broker.StartAsync();
        ConnectionFactory connectionFactory = new() { Uri = new Uri(_broker.GetConnectionString()) };
        _connection = await connectionFactory.CreateConnectionAsync();
        _channel = await _connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true));

        // The AuthSystem's declaration of the exchange, then the TMS's own part.
        await Channel.ExchangeDeclareAsync(AccountEvents.Exchange, ExchangeType.Topic, durable: true, autoDelete: false);
        await AccountEventsTopology.DeclareAsync(Channel, CancellationToken.None);
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

        await _broker.DisposeAsync();
    }

    [Fact]
    public async Task Broker_ShouldNeverParkAMessage_ThatIsNackedMoreOftenThanTheDeliveryLimit()
    {
        // Arrange
        byte[] body = Encoding.UTF8.GetBytes("""{"IdentityUserId":"0199c000-0000-7000-8000-000000000001"}""");
        await PublishAsync(body);
        int nacksToSend = AccountEventsTopology.ErasedDeliveryLimit * 2;

        // Act: the consumer's database-failure path, through a push consumer like production.
        int deliveries = 0;
        TaskCompletionSource allNacked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using IChannel consumerChannel = await _connection!.CreateChannelAsync();
        AsyncEventingBasicConsumer consumer = new(consumerChannel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            if (Interlocked.Increment(ref deliveries) > nacksToSend)
            {
                allNacked.TrySetResult();
                return;
            }

            await consumerChannel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true);
        };
        await consumerChannel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false);
        string consumerTag = await consumerChannel.BasicConsumeAsync(
            queue: AccountEventsTopology.ErasedQueue,
            autoAck: false,
            consumer: consumer);
        await allNacked.Task.WaitAsync(DeliveryTimeout);
        await consumerChannel.BasicCancelAsync(consumerTag);

        // Assert: still delivered after twice the limit, and nothing in the parking lot.
        deliveries.ShouldBeGreaterThan(nacksToSend);
        (await GetWithinTimeoutAsync(AccountEventsTopology.ErasedDeadLetterQueue, EmptyQueueGrace)).ShouldBeNull();
    }

    [Fact]
    public async Task Broker_ShouldParkAMessage_ThatIsRejectedUntilTheDeliveryLimit()
    {
        // Arrange
        byte[] body = Encoding.UTF8.GetBytes("""{"IdentityUserId":"0199c000-0000-7000-8000-000000000002"}""");
        await PublishAsync(body);

        // Act: the consumer's path for a message that keeps breaking the consumer itself.
        await using IChannel consumerChannel = await _connection!.CreateChannelAsync();
        AsyncEventingBasicConsumer consumer = new(consumerChannel);
        consumer.ReceivedAsync += async (_, delivery) =>
            await consumerChannel.BasicRejectAsync(delivery.DeliveryTag, requeue: true);
        await consumerChannel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false);
        string consumerTag = await consumerChannel.BasicConsumeAsync(
            queue: AccountEventsTopology.ErasedQueue,
            autoAck: false,
            consumer: consumer);
        BasicGetResult? dead = await GetWithinTimeoutAsync(AccountEventsTopology.ErasedDeadLetterQueue, DeliveryTimeout);
        await consumerChannel.BasicCancelAsync(consumerTag);

        // Assert
        dead.ShouldNotBeNull();
        dead.Body.ToArray().ShouldBe(body);
    }

    private async Task PublishAsync(byte[] body)
    {
        BasicProperties properties = new()
        {
            MessageId = Guid.NewGuid().ToString(),
            Type = nameof(AccountErased),
            DeliveryMode = DeliveryModes.Persistent
        };

        await Channel.BasicPublishAsync(
            exchange: AccountEvents.Exchange,
            routingKey: AccountEvents.ErasedRoutingKey,
            mandatory: true,
            basicProperties: properties,
            body: body);
    }

    /// <summary>
    /// Dead-lettering happens inside the broker after the reject, so a single immediate get would race it.
    /// </summary>
    private async Task<BasicGetResult?> GetWithinTimeoutAsync(string queue, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (true)
        {
            BasicGetResult? delivery = await Channel.BasicGetAsync(queue, autoAck: true);
            if (delivery is not null || DateTimeOffset.UtcNow >= deadline)
            {
                return delivery;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }
}
