using LotroKoniecDev.AuthSystem.API.BackgroundServices;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Messaging;

/// <summary>
/// Pins the two invariants the redelivery backoff ladder promises only in prose (ADR-0036):
/// the ladder and <see cref="RabbitMqTopology.EmailDeliveryLimit"/> move together, and no rung
/// may hold a delivery unacked long enough for the broker to kill the channel. Without these,
/// <c>Math.Min</c> in the consumer silently absorbs any drift. Also pins the poison decision for
/// unusable message ids (ADR-0037): a delivery the inbox cannot deduplicate must be rejected,
/// not processed blind. And it pins that the consumer stays attached (#1088): a failed attach, a
/// cancellation the client reports on its own included, or a channel the broker closed makes it
/// attach again, one closed look is left to automatic recovery, and only the host's stop ends it.
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

    public static TheoryData<Exception> AttachFailures() => new()
    {
        new TaskCanceledException("The client timed out on its own."),
        new OperationCanceledException("The client gave up on a step."),
        new BrokerUnreachableException(new IOException("Connection refused"))
    };

    [Theory]
    [MemberData(nameof(AttachFailures))]
    public async Task ExecuteAsync_WhenAnAttachFailsWhileTheHostRuns_ShouldAttachAgain(Exception failure)
    {
        // Arrange: the first connect fails while the host runs. A cancellation is how the client
        // reports its own timeout, so only the host's stop token may end the consumer.
        BrokerStub broker = new(true);
        IConnectionFactory connectionFactory = Substitute.For<IConnectionFactory>();
        connectionFactory.CreateConnectionAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IConnection>(failure), Task.FromResult(broker.Connection));
        using EmailDispatchConsumer consumer = CreateConsumer(connectionFactory, TimeSpan.FromMinutes(1));

        // Act
        await consumer.StartAsync(CancellationToken.None);
        bool attached = await broker.WaitUntilConsumingAsync();
        await StopWithinLimitAsync(consumer);

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
        await StopWithinLimitAsync(consumer);

        // Assert
        Task execution = consumer.ExecuteTask.ShouldNotBeNull();
        execution.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_AfterTheBrokerClosedTheChannel_ShouldAttachAgain()
    {
        // Arrange: the broker closes the first channel after the consumer attached, as it does after an
        // ack for a delivery it no longer knows. Automatic recovery does not bring such a channel back.
        BrokerStub closedChannel = new(false);
        BrokerStub openChannel = new(true);
        IConnectionFactory connectionFactory = Substitute.For<IConnectionFactory>();
        connectionFactory.CreateConnectionAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(closedChannel.Connection), Task.FromResult(openChannel.Connection));
        using EmailDispatchConsumer consumer = CreateConsumer(connectionFactory, TimeSpan.FromMilliseconds(10));

        // Act
        await consumer.StartAsync(CancellationToken.None);
        bool attachedAgain = await openChannel.WaitUntilConsumingAsync();
        await StopWithinLimitAsync(consumer);

        // Assert
        attachedAgain.ShouldBeTrue();
    }

    [Theory]
    [InlineData(new[] { true })]
    [InlineData(new[] { false, true })]
    [InlineData(new[] { false, true, false, true })]
    public async Task ExecuteAsync_WhenTheChannelIsNeverClosedTwoLooksInARow_ShouldNotConnectAgain(bool[] channelOpenOnLooks)
    {
        // Arrange: a channel that is closed on single looks at most, the way automatic recovery brings
        // it back after a short loss. The last answer holds for every later look, every 10 ms.
        BrokerStub broker = new(channelOpenOnLooks);
        IConnectionFactory connectionFactory = Substitute.For<IConnectionFactory>();
        connectionFactory.CreateConnectionAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(broker.Connection));
        using EmailDispatchConsumer consumer = CreateConsumer(connectionFactory, TimeSpan.FromMilliseconds(10));

        // Act: about thirty looks
        await consumer.StartAsync(CancellationToken.None);
        await broker.WaitUntilConsumingAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        await StopWithinLimitAsync(consumer);

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
    /// Stops the consumer, but never waits longer than <see cref="WaitLimit"/>, so a consumer that
    /// ignores its stop token never hangs the run. The stop itself returns quietly when the limit runs
    /// out, so the test that is about stopping checks the outcome on its own.
    /// </summary>
    private static async Task StopWithinLimitAsync(EmailDispatchConsumer consumer)
    {
        using CancellationTokenSource stopLimit = new(WaitLimit);
        await consumer.StopAsync(stopLimit.Token);
    }

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

        /// <summary>
        /// Takes what the channel answers when the consumer asks whether it is open, one answer per
        /// look. The last answer holds for every later look.
        /// </summary>
        public BrokerStub(params bool[] channelOpenOnLooks)
        {
            IChannel channel = Substitute.For<IChannel>();
            channel.IsOpen.Returns(channelOpenOnLooks[0], channelOpenOnLooks[1..]);
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
