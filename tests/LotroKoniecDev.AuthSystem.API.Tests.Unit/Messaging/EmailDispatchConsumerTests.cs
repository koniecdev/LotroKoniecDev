using LotroKoniecDev.AuthSystem.API.BackgroundServices;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RabbitMQ.Client;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Messaging;

/// <summary>
/// Pins the two invariants the redelivery backoff ladder promises only in prose (ADR-0036):
/// the ladder and <see cref="RabbitMqTopology.EmailDeliveryLimit"/> move together, and no rung
/// may hold a delivery unacked long enough for the broker to kill the channel. Without these,
/// <c>Math.Min</c> in the consumer silently absorbs any drift. Also pins the poison decision for
/// unusable message ids (ADR-0037): a delivery the inbox cannot deduplicate must be rejected,
/// not processed blind. And it pins that the consumer stays attached (#1088): a cancellation the
/// client reports on its own, or a channel the broker closed, makes it attach again, and only the
/// host's stop ends it.
/// </summary>
public sealed class EmailDispatchConsumerTests
{
    private const string ConsumerTag = "email-consumer";

    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [Fact]
    public void RedeliveryBackoffs_ComparedToDeliveryLimit_HasOneEntryPerAllowedRedelivery()
    {
        EmailDispatchConsumer.RedeliveryBackoffs.Length.ShouldBe(RabbitMqTopology.EmailDeliveryLimit);
    }

    [Fact]
    public void RedeliveryBackoffs_EveryPause_StaysUnderTheBrokerConsumerTimeout()
    {
        // The broker closes the channel when an ack takes longer than consumer_timeout, which is 30
        // minutes by default in RabbitMQ and which compose does not change. A wait at or above that
        // would turn the pause into a lost channel, which uses up a retry instead of waiting one out.
        TimeSpan consumerTimeout = TimeSpan.FromMinutes(30);

        EmailDispatchConsumer.RedeliveryBackoffs.ShouldAllBe(pause => pause < consumerTimeout);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void TryReadMessageId_WithUnusableValue_ReturnsFalse(string? rawMessageId)
    {
        // Arrange: BasicProperties implements IReadOnlyBasicProperties
        BasicProperties properties = new() { MessageId = rawMessageId };

        // Act
        bool usable = EmailDispatchConsumer.TryReadMessageId(properties, out Guid _);

        // Assert
        usable.ShouldBeFalse();
    }

    [Fact]
    public void TryReadMessageId_WithGuidValue_ReturnsTheParsedId()
    {
        // Arrange
        Guid messageId = Guid.CreateVersion7();
        BasicProperties properties = new() { MessageId = messageId.ToString() };

        // Act
        bool usable = EmailDispatchConsumer.TryReadMessageId(properties, out Guid parsed);

        // Assert
        usable.ShouldBeTrue();
        parsed.ShouldBe(messageId);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTheClientCancelsTheAttachOnItsOwn_ShouldAttachAgain()
    {
        // Arrange: the first connect ends in a cancellation the host did not ask for, the way the
        // client reports its own timeout.
        BrokerStub broker = new(channelOpen: true);
        IConnectionFactory connectionFactory = Substitute.For<IConnectionFactory>();
        connectionFactory.CreateConnectionAsync(Arg.Any<CancellationToken>())
            .Returns(
                Task.FromCanceled<IConnection>(new CancellationToken(canceled: true)),
                Task.FromResult(broker.Connection));
        using EmailDispatchConsumer consumer = CreateConsumer(connectionFactory, TimeSpan.FromMinutes(1));

        // Act
        await consumer.StartAsync(CancellationToken.None);
        bool attached = await broker.WaitUntilConsumingAsync();
        await consumer.StopAsync(CancellationToken.None);

        // Assert
        attached.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_WhenTheHostStopsDuringTheAttach_ShouldEndWithoutAnError()
    {
        // Arrange: a broker that never answers, so the connect waits until the host stops it.
        TaskCompletionSource connecting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<IConnection> neverAnswers = new();
        IConnectionFactory connectionFactory = Substitute.For<IConnectionFactory>();
        connectionFactory.CreateConnectionAsync(Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                connecting.TrySetResult();
                return neverAnswers.Task.WaitAsync(callInfo.Arg<CancellationToken>());
            });
        using EmailDispatchConsumer consumer = CreateConsumer(connectionFactory, TimeSpan.FromMinutes(1));

        // Act
        await consumer.StartAsync(CancellationToken.None);
        await connecting.Task.WaitAsync(WaitLimit);
        await consumer.StopAsync(CancellationToken.None);

        // Assert
        Task execution = consumer.ExecuteTask.ShouldNotBeNull();
        execution.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_AfterTheBrokerClosedTheChannel_ShouldAttachAgain()
    {
        // Arrange: the broker closes the first channel after the consumer attached, as it does after an
        // ack for a delivery it no longer knows. Automatic recovery does not bring such a channel back.
        BrokerStub closedChannel = new(channelOpen: false);
        BrokerStub openChannel = new(channelOpen: true);
        IConnectionFactory connectionFactory = Substitute.For<IConnectionFactory>();
        connectionFactory.CreateConnectionAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(closedChannel.Connection), Task.FromResult(openChannel.Connection));
        using EmailDispatchConsumer consumer = CreateConsumer(connectionFactory, TimeSpan.FromMilliseconds(10));

        // Act
        await consumer.StartAsync(CancellationToken.None);
        bool attachedAgain = await openChannel.WaitUntilConsumingAsync();
        await consumer.StopAsync(CancellationToken.None);

        // Assert
        attachedAgain.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_WhileTheConsumerStaysAttached_ShouldNotConnectAgain()
    {
        // Arrange: an open channel and a running subscription, looked at every 10 ms.
        BrokerStub broker = new(channelOpen: true);
        IConnectionFactory connectionFactory = Substitute.For<IConnectionFactory>();
        connectionFactory.CreateConnectionAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(broker.Connection));
        using EmailDispatchConsumer consumer = CreateConsumer(connectionFactory, TimeSpan.FromMilliseconds(10));

        // Act: about thirty looks
        await consumer.StartAsync(CancellationToken.None);
        await broker.WaitUntilConsumingAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        await consumer.StopAsync(CancellationToken.None);

        // Assert: a new connection is a side effect the consumer returns nothing about
        await connectionFactory.Received(1).CreateConnectionAsync(Arg.Any<CancellationToken>());
    }

    private static EmailDispatchConsumer CreateConsumer(IConnectionFactory connectionFactory, TimeSpan watchInterval) =>
        new(
            connectionFactory,
            Substitute.For<IServiceScopeFactory>(),
            NullLogger<EmailDispatchConsumer>.Instance,
            [TimeSpan.Zero],
            watchInterval);

    /// <summary>
    /// One connection with one channel, standing in for the broker client. The channel confirms a
    /// subscription the way the broker does, so the consumer counts as running, and the test can wait
    /// for that moment.
    /// </summary>
    private sealed class BrokerStub
    {
        private readonly TaskCompletionSource _consuming = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IConnection Connection { get; }

        public async Task<bool> WaitUntilConsumingAsync()
        {
            Task finished = await Task.WhenAny(_consuming.Task, Task.Delay(WaitLimit));
            return finished == _consuming.Task;
        }

        public BrokerStub(bool channelOpen)
        {
            IChannel channel = Substitute.For<IChannel>();
            channel.IsOpen.Returns(channelOpen);
            channel.BasicConsumeAsync(
                    Arg.Any<string>(),
                    Arg.Any<bool>(),
                    Arg.Any<string>(),
                    Arg.Any<bool>(),
                    Arg.Any<bool>(),
                    Arg.Any<IDictionary<string, object?>?>(),
                    Arg.Any<IAsyncBasicConsumer>(),
                    Arg.Any<CancellationToken>())
                .Returns(async callInfo =>
                {
                    await callInfo.Arg<IAsyncBasicConsumer>().HandleBasicConsumeOkAsync(ConsumerTag);
                    _consuming.TrySetResult();
                    return ConsumerTag;
                });

            Connection = Substitute.For<IConnection>();
            Connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(channel));
        }
    }
}
