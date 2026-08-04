using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Messaging;

/// <summary>
/// Exercises the real <see cref="RabbitMqMessagePublisher"/> against a real broker — the only
/// suite where its lazy connect, topology declaration, publisher confirmations, mandatory
/// returns and channel rebuild actually run (everywhere else the factory swaps in
/// <c>SpyMessagePublisher</c>, and <c>DeadLetterTopologyTests</c> drives raw channels).
/// </summary>
public sealed class RabbitMqMessagePublisherTests : IClassFixture<RabbitMqBrokerFixture>, IAsyncLifetime
{
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(10);

    private const string Payload = """{"IdentityUserId":"a3a2cbb0-0000-0000-0000-000000000001"}""";

    private readonly RabbitMqBrokerFixture _broker;
    private RabbitMqMessagePublisher _publisher = null!;

    public RabbitMqMessagePublisherTests(RabbitMqBrokerFixture broker)
    {
        _broker = broker;
    }

    public Task InitializeAsync()
    {
        _publisher = new RabbitMqMessagePublisher(
            Microsoft.Extensions.Options.Options.Create(_broker.BuildOptions()),
            TimeProvider.System,
            NullLogger<RabbitMqMessagePublisher>.Instance);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _publisher.DisposeAsync();
    }

    [Fact]
    public async Task PublishAsync_ShouldDeliverThePersistentMessage_WhenTheRoutingKeyIsBound()
    {
        // Arrange
        Guid messageId = Guid.CreateVersion7();

        // Act — first publish on a fresh instance also opens the connection and declares topology
        await _publisher.PublishAsync(
            RabbitMqTopology.EmailConfirmationRoutingKey, Payload, messageId, CancellationToken.None);

        // Assert — the broker took responsibility and the queue holds the exact wire message
        BasicGetResult delivery = (await GetFromEmailQueueAsync()).ShouldNotBeNull();
        Encoding.UTF8.GetString(delivery.Body.ToArray()).ShouldBe(Payload);
        delivery.BasicProperties.MessageId.ShouldBe(messageId.ToString());
        delivery.BasicProperties.DeliveryMode.ShouldBe(DeliveryModes.Persistent);
        delivery.BasicProperties.ContentType.ShouldBe("application/json");
        delivery.BasicProperties.ContentEncoding.ShouldBe(Encoding.UTF8.WebName);
    }

    [Fact]
    public async Task PublishAsync_ShouldSurfaceTheReturn_WhenNoQueueIsBoundToTheRoutingKey()
    {
        // Act — "billing.invoice" matches no binding (the queue binds email.#), and the publisher
        // sends mandatory with confirmation tracking, so the basic.return must fault this very call
        // instead of silently dropping the message
        Task publish = _publisher.PublishAsync(
            "billing.invoice", Payload, Guid.CreateVersion7(), CancellationToken.None);

        // Assert
        await Should.ThrowAsync<PublishException>(publish);
    }

    [Fact]
    public async Task PublishAsync_ShouldRebuildAndDeliver_WhenTheBrokerDropsEveryConnection()
    {
        // Arrange — a first publish so the long-lived connection and channel exist to be killed
        await _publisher.PublishAsync(
            RabbitMqTopology.EmailConfirmationRoutingKey, Payload, Guid.CreateVersion7(), CancellationToken.None);
        (await GetFromEmailQueueAsync()).ShouldNotBeNull();

        await _broker.CloseAllConnectionsAsync();

        // Act — the relay's behavior on a failed pass is retry-with-backoff, so eventual success
        // is the contract; the first attempt may still race the client noticing the dead socket
        Guid messageId = Guid.CreateVersion7();
        await PublishWithRetryAsync(messageId);

        // Assert
        BasicGetResult delivery = (await GetFromEmailQueueAsync()).ShouldNotBeNull();
        delivery.BasicProperties.MessageId.ShouldBe(messageId.ToString());
    }

    private async Task PublishWithRetryAsync(Guid messageId)
    {
        Stopwatch elapsed = Stopwatch.StartNew();

        while (true)
        {
            try
            {
                await _publisher.PublishAsync(
                    RabbitMqTopology.EmailConfirmationRoutingKey, Payload, messageId, CancellationToken.None);
                return;
            }
            catch (Exception) when (elapsed.Elapsed < DeliveryTimeout)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
        }
    }

    /// <summary>
    /// Reads one delivery off the e-mail queue over a fresh connection (the publisher under test
    /// owns its own), polling because routing inside the broker is asynchronous.
    /// </summary>
    private async Task<BasicGetResult?> GetFromEmailQueueAsync()
    {
        await using IConnection connection = await _broker.ConnectAsync(CancellationToken.None);
        await using IChannel channel = await connection.CreateChannelAsync();

        Stopwatch elapsed = Stopwatch.StartNew();

        while (true)
        {
            BasicGetResult? delivery = await channel.BasicGetAsync(RabbitMqTopology.EmailQueue, autoAck: true);
            if (delivery is not null)
            {
                return delivery;
            }

            if (elapsed.Elapsed >= DeliveryTimeout)
            {
                return null;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }
}
