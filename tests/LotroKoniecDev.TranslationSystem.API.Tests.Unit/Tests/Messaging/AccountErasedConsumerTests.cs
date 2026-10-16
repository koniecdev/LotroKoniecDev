using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Enums;
using LotroKoniecDev.SharedKernel.IntegrationEvents;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Features.Translators;
using LotroKoniecDev.TranslationSystem.API.Messaging;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Messaging;

/// <summary>
/// Pins what the consumer promises only in prose: no pause outlives the broker's consumer timeout, the
/// pauses only grow, a payload it can never handle is refused instead of erasing a profile at random,
/// and a channel that fails under it never leaves a delivery stuck. It also pins that the consumer stays
/// attached (ADR-0065 §4), as the e-mail consumer's tests do (#1088): a failed attach, a cancellation the
/// client reports on its own included, a channel the broker closed or a subscription it cancelled makes
/// it attach again without leaving the old connection behind, one closed look is left to automatic
/// recovery, and only the host's stop ends it.
/// </summary>
public sealed class AccountErasedConsumerTests
{
    private const ulong DeliveryTag = 42;
    private const string ConsumerTag = "account-consumer";

    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [Fact]
    public void RetryBackoffs_EveryPause_StaysUnderTheBrokerConsumerTimeout()
    {
        // RabbitMQ closes the channel when an ack takes longer than consumer_timeout, 30 minutes by
        // default, and compose does not change it. A lost channel counts against the delivery limit,
        // which a database outage must never reach.
        TimeSpan consumerTimeout = TimeSpan.FromMinutes(30);

        AccountErasedConsumer.RetryBackoffs.ShouldAllBe(pause => pause < consumerTimeout);
    }

    [Fact]
    public void RetryBackoffs_FromFirstToLast_NeverShrink()
    {
        TimeSpan[] pauses = AccountErasedConsumer.RetryBackoffs;

        pauses.ShouldNotBeEmpty();
        pauses.Zip(pauses.Skip(1)).ShouldAllBe(pair => pair.First <= pair.Second);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"IdentityUserId":"00000000-0000-0000-0000-000000000000"}""")]
    [InlineData("""{"IdentityUserId":"not-a-guid"}""")]
    [InlineData("""{"IdentityUserId":42}""")]
    public void TryDeserialize_WithUnusablePayload_ReturnsNull(string payload)
    {
        // Act
        AccountErased? message = AccountErasedConsumer.TryDeserialize(Encoding.UTF8.GetBytes(payload));

        // Assert
        message.ShouldBeNull();
    }

    [Fact]
    public void TryDeserialize_WithThePayloadTheAuthSystemWrites_ReturnsTheAccountId()
    {
        // Arrange: the outbox writer serializes the shared record with the default options.
        Guid identityUserId = Guid.CreateVersion7();
        string payload = JsonSerializer.Serialize(new AccountErased(identityUserId));

        // Act
        AccountErased? message = AccountErasedConsumer.TryDeserialize(Encoding.UTF8.GetBytes(payload));

        // Assert
        message.ShouldNotBeNull();
        message.IdentityUserId.ShouldBe(identityUserId);
    }

    [Fact]
    public async Task OnDeliveredAsync_WhenTheAckFails_ShouldPutTheDeliveryBackWithAReject()
    {
        // Arrange: the profile is erased, then the channel fails under the ack.
        IChannel channel = Substitute.For<IChannel>();
        channel.BasicAckAsync(DeliveryTag, false, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException(new InvalidOperationException("channel closed")));
        AccountErasedConsumer consumer = CreateConsumer();

        // Act
        await consumer.OnDeliveredAsync(channel, Delivery(), CancellationToken.None);

        // Assert: a reject, so a message that keeps breaking the consumer ends at the delivery limit.
        await channel.Received(1).BasicRejectAsync(DeliveryTag, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnDeliveredAsync_WhenTheAckAndTheRejectBothFail_ShouldNotThrow()
    {
        // Arrange: a dead channel. An exception leaving the handler would be swallowed by the client
        // library; the broker puts the unacknowledged delivery back when the channel closes.
        IChannel channel = Substitute.For<IChannel>();
        channel.BasicAckAsync(DeliveryTag, false, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException(new InvalidOperationException("channel closed")));
        channel.BasicRejectAsync(DeliveryTag, true, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException(new InvalidOperationException("channel closed")));
        AccountErasedConsumer consumer = CreateConsumer();

        // Act & Assert
        await Should.NotThrowAsync(() => consumer.OnDeliveredAsync(channel, Delivery(), CancellationToken.None));
    }

    [Fact]
    public async Task OnDeliveredAsync_WhenTheDatabaseIsUnavailable_ShouldReturnTheMessageWithANack()
    {
        // Arrange
        IChannel channel = Substitute.For<IChannel>();
        AccountErasedConsumer consumer = CreateConsumer(new StubEraseHandler(() => throw new TimeoutException("database down")));

        // Act
        await consumer.OnDeliveredAsync(channel, Delivery(), CancellationToken.None);

        // Assert: a nack, which the delivery limit does not count, so an outage never parks an erasure.
        await channel.Received(1).BasicNackAsync(DeliveryTag, false, true, Arg.Any<CancellationToken>());
        await channel.DidNotReceiveWithAnyArgs().BasicRejectAsync(default, default, default);
    }

    [Fact]
    public async Task OnDeliveredAsync_WhenTheErasureFailsForAnotherReason_ShouldReturnTheMessageWithAReject()
    {
        // Arrange: a bug fails the same way every time, so it has to reach the delivery limit and park.
        IChannel channel = Substitute.For<IChannel>();
        AccountErasedConsumer consumer = CreateConsumer(new StubEraseHandler(() => throw new InvalidOperationException("bug")));

        // Act
        await consumer.OnDeliveredAsync(channel, Delivery(), CancellationToken.None);

        // Assert
        await channel.Received(1).BasicRejectAsync(DeliveryTag, true, Arg.Any<CancellationToken>());
        await channel.DidNotReceiveWithAnyArgs().BasicNackAsync(default, default, default, default);
    }

    [Fact]
    public async Task OnDeliveredAsync_WhenTheCommandIsRefused_ShouldParkTheMessageAtOnce()
    {
        // Arrange: sending a refused command again can never change the answer.
        IChannel channel = Substitute.For<IChannel>();
        AccountErasedConsumer consumer = CreateConsumer(new StubEraseHandler(
            () => Result.Failure(new Error("Translators.Validation", "refused", TypeOfError.Validation))));

        // Act
        await consumer.OnDeliveredAsync(channel, Delivery(), CancellationToken.None);

        // Assert
        await channel.Received(1).BasicRejectAsync(DeliveryTag, false, Arg.Any<CancellationToken>());
        await channel.DidNotReceiveWithAnyArgs().BasicAckAsync(default, default, default);
    }

    [Fact]
    public async Task OnDeliveredAsync_WhenTheProfileIsErased_ShouldAck()
    {
        // Arrange
        IChannel channel = Substitute.For<IChannel>();
        AccountErasedConsumer consumer = CreateConsumer();

        // Act
        await consumer.OnDeliveredAsync(channel, Delivery(), CancellationToken.None);

        // Assert
        await channel.Received(1).BasicAckAsync(DeliveryTag, false, Arg.Any<CancellationToken>());
    }

    public static TheoryData<Exception> DatabaseOutages() => new()
    {
        new TimeoutException("timed out"),
        new System.Net.Sockets.SocketException(),
        new Npgsql.NpgsqlException("connection lost", new TimeoutException()),
        new Npgsql.PostgresException("password authentication failed", "FATAL", "FATAL", "28P01"),
        new Npgsql.PostgresException("permission denied for table Translators", "ERROR", "ERROR", "42501"),
        new Npgsql.PostgresException("relation does not exist", "ERROR", "ERROR", "42P01"),
        new Microsoft.EntityFrameworkCore.DbUpdateException("save failed", new Npgsql.PostgresException("internal error", "ERROR", "ERROR", "XX000")),
        new Microsoft.EntityFrameworkCore.Storage.RetryLimitExceededException("retries spent", new InvalidOperationException()),
        new Microsoft.EntityFrameworkCore.DbUpdateException("save failed", new TimeoutException())
    };

    [Theory]
    [MemberData(nameof(DatabaseOutages))]
    public void IsDatabaseUnavailable_ForAnOutage_ReturnsTrue(Exception exception)
    {
        AccountErasedConsumer.IsDatabaseUnavailable(exception).ShouldBeTrue();
    }

    public static TheoryData<Exception> OtherFailures() => new()
    {
        new InvalidOperationException("bug"),
        new ArgumentException("bad argument"),
        new Microsoft.EntityFrameworkCore.DbUpdateException("lost update", new InvalidOperationException()),
        new Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException("another write changed the row")
    };

    [Theory]
    [MemberData(nameof(OtherFailures))]
    public void IsDatabaseUnavailable_ForAnotherFailure_ReturnsFalse(Exception exception)
    {
        AccountErasedConsumer.IsDatabaseUnavailable(exception).ShouldBeFalse();
    }

    [Fact]
    public void BuildConnectionFactory_ForAnySettings_TurnsOnAutomaticAndTopologyRecovery()
    {
        // Arrange: the watch leaves a single closed look to automatic recovery, so recovery must be on.
        RabbitMqSettings settings = new()
        {
            Host = "broker",
            Username = "rabbitmq",
            Password = "a-broker-password"
        };

        // Act
        ConnectionFactory connectionFactory = AccountErasedConsumer.BuildConnectionFactory(settings);

        // Assert
        connectionFactory.AutomaticRecoveryEnabled.ShouldBeTrue();
        connectionFactory.TopologyRecoveryEnabled.ShouldBeTrue();
    }

    public static TheoryData<TimeSpan[], TimeSpan[], TimeSpan> UnusableTimings() => new()
    {
        { [], [TimeSpan.Zero], TimeSpan.FromSeconds(1) },
        { [TimeSpan.Zero, TimeSpan.FromSeconds(-1)], [TimeSpan.Zero], TimeSpan.FromSeconds(1) },
        { [TimeSpan.Zero], [], TimeSpan.FromSeconds(1) },
        { [TimeSpan.Zero], [TimeSpan.FromSeconds(-1), TimeSpan.Zero], TimeSpan.FromSeconds(1) },
        { [TimeSpan.Zero], [TimeSpan.Zero], TimeSpan.Zero },
        { [TimeSpan.Zero], [TimeSpan.Zero], TimeSpan.FromSeconds(-1) }
    };

    [Theory]
    [MemberData(nameof(UnusableTimings))]
    public void Constructor_WithAnUnusableTiming_ShouldThrow(
        TimeSpan[] connectBackoffs,
        TimeSpan[] retryBackoffs,
        TimeSpan watchInterval)
    {
        // Act & Assert: a pause or a watch the consumer cannot wait out would only fail later, in a loop.
        Should.Throw<ArgumentOutOfRangeException>(() => new AccountErasedConsumer(
            Substitute.For<IConnectionFactory>(),
            Substitute.For<IServiceScopeFactory>(),
            NullLogger<AccountErasedConsumer>.Instance,
            connectBackoffs,
            retryBackoffs,
            watchInterval));
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
        using AccountErasedConsumer consumer = CreateConsumer(connectionFactory, TimeSpan.FromMinutes(1));

        // Act
        await consumer.StartAsync(CancellationToken.None);
        bool attached = await broker.WaitUntilConsumingAsync();
        await StopWithinLimitAsync(consumer);

        // Assert
        attached.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_WhenAnAttachFailsAfterTheConnect_ShouldDisposeThatConnectionBeforeTheNextAttempt()
    {
        // Arrange: the connection opens, but the channel does not. Left alive, automatic recovery would
        // keep that half-attached connection open for good.
        BrokerStub refusedChannel = BrokerStub.RefusingTheChannel();
        BrokerStub openChannel = new(true);
        bool disposedBeforeTheNextAttempt = false;
        IConnectionFactory connectionFactory = Substitute.For<IConnectionFactory>();
        connectionFactory.CreateConnectionAsync(Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromResult(refusedChannel.Connection),
                _ =>
                {
                    disposedBeforeTheNextAttempt = refusedChannel.IsConnectionDisposed;
                    return Task.FromResult(openChannel.Connection);
                });
        using AccountErasedConsumer consumer = CreateConsumer(connectionFactory, TimeSpan.FromMinutes(1));

        // Act
        await consumer.StartAsync(CancellationToken.None);
        bool attached = await openChannel.WaitUntilConsumingAsync();
        await StopWithinLimitAsync(consumer);

        // Assert
        attached.ShouldBeTrue();
        disposedBeforeTheNextAttempt.ShouldBeTrue();
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
        using AccountErasedConsumer consumer = CreateConsumer(connectionFactory, TimeSpan.FromMinutes(1));

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
        using AccountErasedConsumer consumer = CreateConsumer(connectionFactory, TimeSpan.FromMilliseconds(10));

        // Act
        await consumer.StartAsync(CancellationToken.None);
        bool attachedAgain = await openChannel.WaitUntilConsumingAsync();
        await StopWithinLimitAsync(consumer);

        // Assert
        attachedAgain.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_AfterTheBrokerCancelledTheSubscription_ShouldAttachAgain()
    {
        // Arrange: the channel stays open, but the broker cancels the subscription, as it does when the
        // queue is deleted to change its arguments. Automatic recovery does not bring that back.
        BrokerStub cancelledSubscription = new(true);
        BrokerStub newSubscription = new(true);
        IConnectionFactory connectionFactory = Substitute.For<IConnectionFactory>();
        connectionFactory.CreateConnectionAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(cancelledSubscription.Connection), Task.FromResult(newSubscription.Connection));
        using AccountErasedConsumer consumer = CreateConsumer(connectionFactory, TimeSpan.FromMilliseconds(10));

        // Act
        await consumer.StartAsync(CancellationToken.None);
        await cancelledSubscription.WaitUntilConsumingAsync();
        await cancelledSubscription.CancelSubscriptionAsync();
        bool attachedAgain = await newSubscription.WaitUntilConsumingAsync();
        await StopWithinLimitAsync(consumer);

        // Assert
        attachedAgain.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_BeforeItAttachesAgain_ShouldDisposeTheOldConnection()
    {
        // Arrange: a detached consumer whose old connection stayed alive would come back through
        // automatic recovery and consume next to the new one.
        BrokerStub closedChannel = new(false);
        BrokerStub openChannel = new(true);
        bool disposedBeforeTheNextAttempt = false;
        IConnectionFactory connectionFactory = Substitute.For<IConnectionFactory>();
        connectionFactory.CreateConnectionAsync(Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromResult(closedChannel.Connection),
                _ =>
                {
                    disposedBeforeTheNextAttempt = closedChannel.IsConnectionDisposed;
                    return Task.FromResult(openChannel.Connection);
                });
        using AccountErasedConsumer consumer = CreateConsumer(connectionFactory, TimeSpan.FromMilliseconds(10));

        // Act
        await consumer.StartAsync(CancellationToken.None);
        bool attachedAgain = await openChannel.WaitUntilConsumingAsync();
        await StopWithinLimitAsync(consumer);

        // Assert
        attachedAgain.ShouldBeTrue();
        disposedBeforeTheNextAttempt.ShouldBeTrue();
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
        using AccountErasedConsumer consumer = CreateConsumer(connectionFactory, TimeSpan.FromMilliseconds(10));

        // Act
        await consumer.StartAsync(CancellationToken.None);
        bool lookedThirtyTimes = await broker.WaitForLooksAsync(30);
        await StopWithinLimitAsync(consumer);

        // Assert: a new connection is a side effect the consumer returns nothing about
        lookedThirtyTimes.ShouldBeTrue();
        await connectionFactory.Received(1).CreateConnectionAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenTheChannelIsClosedExactlyTwoLooksInARow_ShouldConnectAgainOnce()
    {
        // Arrange: the second closed look in a row is the one that attaches again, not the first and not
        // a third. The channel is open on every look after the second, every 10 ms.
        BrokerStub broker = new(false, false, true);
        IConnectionFactory connectionFactory = Substitute.For<IConnectionFactory>();
        connectionFactory.CreateConnectionAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(broker.Connection));
        using AccountErasedConsumer consumer = CreateConsumer(connectionFactory, TimeSpan.FromMilliseconds(10));

        // Act
        await consumer.StartAsync(CancellationToken.None);
        bool lookedThirtyTimes = await broker.WaitForLooksAsync(30);
        await StopWithinLimitAsync(consumer);

        // Assert: a new connection is a side effect the consumer returns nothing about
        lookedThirtyTimes.ShouldBeTrue();
        await connectionFactory.Received(2).CreateConnectionAsync(Arg.Any<CancellationToken>());
    }

    private static AccountErasedConsumer CreateConsumer(StubEraseHandler? handler = null)
    {
        StubEraseHandler eraseHandler = handler ?? new StubEraseHandler(Result.Success);
        ServiceProvider services = new ServiceCollection()
            .AddScoped<ICommandHandler<EraseTranslatorProfile.Command, Result>>(_ => eraseHandler)
            .BuildServiceProvider();

        return new AccountErasedConsumer(
            Substitute.For<IConnectionFactory>(),
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AccountErasedConsumer>.Instance,
            [TimeSpan.Zero],
            [TimeSpan.Zero],
            TimeSpan.FromMinutes(1));
    }

    private static AccountErasedConsumer CreateConsumer(IConnectionFactory connectionFactory, TimeSpan watchInterval) =>
        new(
            connectionFactory,
            Substitute.For<IServiceScopeFactory>(),
            NullLogger<AccountErasedConsumer>.Instance,
            [TimeSpan.Zero],
            [TimeSpan.Zero],
            watchInterval);

    /// <summary>
    /// Stops the consumer, but never waits longer than <see cref="WaitLimit"/>, so a consumer that
    /// ignores its stop token never hangs the run. The stop itself returns quietly when the limit runs
    /// out, so the test that is about stopping checks the outcome on its own.
    /// </summary>
    private static async Task StopWithinLimitAsync(AccountErasedConsumer consumer)
    {
        using CancellationTokenSource stopLimit = new(WaitLimit);
        await consumer.StopAsync(stopLimit.Token);
    }

    private static BasicDeliverEventArgs Delivery()
    {
        BasicProperties properties = new()
        {
            MessageId = Guid.NewGuid().ToString(),
            Type = nameof(AccountErased)
        };
        byte[] body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new AccountErased(Guid.NewGuid())));

        return new BasicDeliverEventArgs(
            consumerTag: "test",
            deliveryTag: DeliveryTag,
            redelivered: false,
            exchange: AccountEvents.Exchange,
            routingKey: AccountEvents.ErasedRoutingKey,
            properties: properties,
            body: body);
    }

    /// <summary>
    /// Answers every erasure the way the test says: success by default, or a refusal or an exception.
    /// </summary>
    private sealed class StubEraseHandler : ICommandHandler<EraseTranslatorProfile.Command, Result>
    {
        private readonly Func<Result> _answer;

        public StubEraseHandler(Func<Result> answer)
        {
            _answer = answer;
        }

        public ValueTask<Result> Handle(EraseTranslatorProfile.Command command, CancellationToken cancellationToken) =>
            ValueTask.FromResult(_answer());
    }

    /// <summary>
    /// One connection with one channel, standing in for the broker client. The channel confirms a
    /// subscription the way the broker does, so the consumer counts as running, and the test can wait
    /// for that moment, cancel the subscription as the broker does, count the consumer's looks and see
    /// whether the connection was disposed.
    /// </summary>
    private sealed class BrokerStub
    {
        private readonly bool[] _channelOpenOnLooks;
        private readonly TaskCompletionSource _consuming = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IAsyncBasicConsumer? _consumer;
        private int _looks;
        private int _connectionDisposals;

        public IConnection Connection { get; }

        public bool IsConnectionDisposed => Volatile.Read(ref _connectionDisposals) > 0;

        public async Task<bool> WaitUntilConsumingAsync()
        {
            Task finished = await Task.WhenAny(_consuming.Task, Task.Delay(WaitLimit));
            return finished == _consuming.Task;
        }

        public Task CancelSubscriptionAsync()
        {
            IAsyncBasicConsumer consumer = _consumer
                ?? throw new InvalidOperationException("Nothing has subscribed yet.");
            return consumer.HandleBasicCancelAsync(ConsumerTag);
        }

        public async Task<bool> WaitForLooksAsync(int looks)
        {
            using CancellationTokenSource waitLimit = new(WaitLimit);

            while (Volatile.Read(ref _looks) < looks && !waitLimit.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5));
            }

            return Volatile.Read(ref _looks) >= looks;
        }

        /// <summary>
        /// A connection that opens, but whose channel the broker refuses.
        /// </summary>
        public static BrokerStub RefusingTheChannel()
        {
            BrokerStub broker = new(true);
            broker.Connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<IChannel>(new InvalidOperationException("The broker refused the channel.")));
            return broker;
        }

        /// <summary>
        /// Takes what the channel answers when the consumer asks whether it is open, one answer per
        /// look. The last answer holds for every later look.
        /// </summary>
        public BrokerStub(params bool[] channelOpenOnLooks)
        {
            _channelOpenOnLooks = channelOpenOnLooks;

            IChannel channel = Substitute.For<IChannel>();
            channel.IsOpen.Returns(_ => AnswerNextLook());
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
                    IAsyncBasicConsumer consumer = callInfo.Arg<IAsyncBasicConsumer>();
                    await consumer.HandleBasicConsumeOkAsync(ConsumerTag);
                    _consumer = consumer;
                    _consuming.TrySetResult();
                    return ConsumerTag;
                });

            Connection = Substitute.For<IConnection>();
            Connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(channel));
            Connection.DisposeAsync().Returns(_ =>
            {
                Interlocked.Increment(ref _connectionDisposals);
                return ValueTask.CompletedTask;
            });
        }

        private bool AnswerNextLook()
        {
            int look = Interlocked.Increment(ref _looks);
            return _channelOpenOnLooks[Math.Min(look, _channelOpenOnLooks.Length) - 1];
        }
    }
}
