using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
using LotroKoniecDev.Tests.Shared;
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
/// On a fake clock it pins which pause follows which failure (#1223): the pause after each database
/// outage in a row, the last pause for every outage after it, the start over after a success, the
/// Error level once the pauses stop growing, the first pause after any other error, a stop during a
/// pause, the pause between connection attempts and the watch interval.
/// </summary>
public sealed class AccountErasedConsumerTests
{
    private const ulong DeliveryTag = 42;
    private const string ConsumerTag = "account-consumer";

    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OneMillisecond = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// The pause after each database outage in a row, up to the last one and then the last one again.
    /// </summary>
    private static readonly TimeSpan[] PausesUpToTheLastAndOnceMore =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(15)
    ];

    private readonly TimerWatchingTimeProvider _clock = new();

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

    [Fact]
    public async Task OnDeliveredAsync_WhenTheDatabaseIsUnavailable_ShouldNackOnlyOnceThePauseRanOut()
    {
        // Arrange
        IChannel channel = Substitute.For<IChannel>();
        AccountErasedConsumer consumer = CreateConsumerOnTheFakeClock(
            new StubEraseHandler(() => throw new TimeoutException("database down")),
            NullLoggerFactory.Instance);

        // Act
        Task delivering = consumer.OnDeliveredAsync(channel, Delivery(), CancellationToken.None);
        await _clock.WaitForNextTimerAsync();
        _clock.Advance(TimeSpan.FromSeconds(30) - OneMillisecond);
        int wakeUpsJustBeforeThePauseEnds = _clock.WakeUps;
        int channelCallsJustBeforeThePauseEnds = channel.ReceivedCalls().Count();
        _clock.Advance(OneMillisecond);
        await delivering.WaitAsync(WaitLimit);

        // Assert: the channel hears nothing until the pause runs out, and then the message goes back
        wakeUpsJustBeforeThePauseEnds.ShouldBe(0);
        channelCallsJustBeforeThePauseEnds.ShouldBe(0);
        await channel.Received(1).BasicNackAsync(DeliveryTag, false, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnDeliveredAsync_WhenTheErasureFailsForAnotherReason_ShouldRejectOnlyOnceThePauseRanOut()
    {
        // Arrange
        IChannel channel = Substitute.For<IChannel>();
        AccountErasedConsumer consumer = CreateConsumerOnTheFakeClock(
            new StubEraseHandler(() => throw new InvalidOperationException("bug")),
            NullLoggerFactory.Instance);

        // Act
        Task delivering = consumer.OnDeliveredAsync(channel, Delivery(), CancellationToken.None);
        await _clock.WaitForNextTimerAsync();
        _clock.Advance(TimeSpan.FromSeconds(30) - OneMillisecond);
        int wakeUpsJustBeforeThePauseEnds = _clock.WakeUps;
        int channelCallsJustBeforeThePauseEnds = channel.ReceivedCalls().Count();
        _clock.Advance(OneMillisecond);
        await delivering.WaitAsync(WaitLimit);

        // Assert: the channel hears nothing until the pause runs out, and then the message goes back
        wakeUpsJustBeforeThePauseEnds.ShouldBe(0);
        channelCallsJustBeforeThePauseEnds.ShouldBe(0);
        await channel.Received(1).BasicRejectAsync(DeliveryTag, true, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A stop in the middle of a pause settles nothing: the broker puts the unacknowledged delivery back
    /// when the channel closes, and the next start handles it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnDeliveredAsync_WhenTheHostStopsDuringThePause_ShouldEndWithoutSettlingTheDelivery(
        bool theDatabaseIsUnavailable)
    {
        // Arrange
        using CancellationTokenSource stopping = new();
        IChannel channel = Substitute.For<IChannel>();
        AccountErasedConsumer consumer = CreateConsumerOnTheFakeClock(
            new StubEraseHandler(() =>
            {
                Exception failure = theDatabaseIsUnavailable
                    ? new TimeoutException("database down")
                    : new InvalidOperationException("bug");
                throw failure;
            }),
            NullLoggerFactory.Instance);

        // Act
        Task delivering = consumer.OnDeliveredAsync(channel, Delivery(), stopping.Token);
        await _clock.WaitForNextTimerAsync();
        await stopping.CancelAsync();
        await delivering.WaitAsync(WaitLimit);

        // Assert: no ack, no nack and no reject
        channel.ReceivedCalls().ShouldBeEmpty();
    }

    /// <summary>
    /// The sixth outage in a row proves the ceiling: once the pauses reach the last one, an outage of any
    /// length is retried at that pace and no faster.
    /// </summary>
    [Fact]
    public async Task OnDeliveredAsync_WhileTheDatabaseStaysUnavailable_ShouldWaitLongerBeforeEachNackUpToTheLastPause()
    {
        // Arrange
        IChannel channel = Substitute.For<IChannel>();
        AccountErasedConsumer consumer = CreateConsumerOnTheFakeClock(
            new StubEraseHandler(() => throw new TimeoutException("database down")),
            NullLoggerFactory.Instance);

        // Act
        IReadOnlyList<int> wakeUpsJustBeforeEachPauseEnds =
            await DeliverThroughPausesAsync(consumer, channel, PausesUpToTheLastAndOnceMore);

        // Assert: no pause ended early, and each delivery finished once its pause ran out
        wakeUpsJustBeforeEachPauseEnds.ShouldBe([0, 1, 2, 3, 4, 5]);
    }

    /// <summary>
    /// Two outages in a row, then an erasure that works, then an outage again: the third pause is the
    /// first one again, not the third one.
    /// </summary>
    [Fact]
    public async Task OnDeliveredAsync_AfterAnErasureSucceeds_ShouldStartThePausesOverFromTheFirst()
    {
        // Arrange
        int attempts = 0;
        IChannel channel = Substitute.For<IChannel>();
        AccountErasedConsumer consumer = CreateConsumerOnTheFakeClock(
            new StubEraseHandler(() => ++attempts == 3 ? Result.Success() : throw new TimeoutException("database down")),
            NullLoggerFactory.Instance);

        // Act
        await DeliverThroughPausesAsync(consumer, channel, [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)]);
        await consumer.OnDeliveredAsync(channel, Delivery(), CancellationToken.None).WaitAsync(WaitLimit);
        IReadOnlyList<int> wakeUpsJustBeforeTheNextPauseEnds =
            await DeliverThroughPausesAsync(consumer, channel, [TimeSpan.FromSeconds(30)]);

        // Assert
        wakeUpsJustBeforeTheNextPauseEnds.ShouldBe([2]);
    }

    /// <summary>
    /// The level tells an operator when to look: a Warning while the pauses still grow, and an Error
    /// from the last pause on, because by then the outage is long enough for a person.
    /// </summary>
    [Fact]
    public async Task OnDeliveredAsync_WhileTheDatabaseStaysUnavailable_ShouldLogAnErrorOnlyFromTheLastPauseOn()
    {
        // Arrange
        using CapturingLoggerFactory loggerFactory = new();
        IChannel channel = Substitute.For<IChannel>();
        AccountErasedConsumer consumer = CreateConsumerOnTheFakeClock(
            new StubEraseHandler(() => throw new TimeoutException("database down")),
            loggerFactory);

        // Act
        await DeliverThroughPausesAsync(consumer, channel, PausesUpToTheLastAndOnceMore);

        // Assert
        loggerFactory.Entries
            .Where(entry => entry.EventId.Id == EventIds.AccountConsumerEraseFailed)
            .Select(entry => entry.Level)
            .ShouldBe([LogLevel.Warning, LogLevel.Warning, LogLevel.Warning, LogLevel.Warning, LogLevel.Error, LogLevel.Error]);
    }

    /// <summary>
    /// Two outages in a row, then a bug. A bug tells nothing about the database, so its pause stays at
    /// the first one whatever came before.
    /// </summary>
    [Fact]
    public async Task OnDeliveredAsync_AfterAnErrorThatIsNotAnOutage_ShouldRequeueOnlyOnceTheFirstPauseRanOut()
    {
        // Arrange
        int attempts = 0;
        IChannel channel = Substitute.For<IChannel>();
        AccountErasedConsumer consumer = CreateConsumerOnTheFakeClock(
            new StubEraseHandler(() =>
            {
                Exception failure = ++attempts <= 2
                    ? new TimeoutException("database down")
                    : new InvalidOperationException("bug");
                throw failure;
            }),
            NullLoggerFactory.Instance);

        // Act
        IReadOnlyList<int> wakeUpsJustBeforeEachPauseEnds = await DeliverThroughPausesAsync(
            consumer,
            channel,
            [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(30)]);

        // Assert
        wakeUpsJustBeforeEachPauseEnds.ShouldBe([0, 1, 2]);
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
    public void BuildConnectionFactory_FromTheSettings_ConnectsWhereTheyPoint()
    {
        // Arrange: no value is the client's default, so a setting the factory drops shows up.
        RabbitMqSettings settings = new()
        {
            Host = "broker.internal",
            Port = 5673,
            Username = "tms-consumer",
            Password = "a-broker-password",
            VirtualHost = "/lotro"
        };

        // Act
        ConnectionFactory connectionFactory = AccountErasedConsumer.BuildConnectionFactory(settings);

        // Assert
        connectionFactory.HostName.ShouldBe("broker.internal");
        connectionFactory.Port.ShouldBe(5673);
        connectionFactory.UserName.ShouldBe("tms-consumer");
        connectionFactory.Password.ShouldBe("a-broker-password");
        connectionFactory.VirtualHost.ShouldBe("/lotro");
    }

    [Fact]
    public void BuildConnectionFactory_FromTheSettings_TurnsOnAutomaticAndTopologyRecovery()
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
            TimeProvider.System,
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
    public async Task ExecuteAsync_WhenTheAttachFailsMoreTimesThanThereArePauses_ShouldKeepRetrying()
    {
        // Arrange: four failed connects against two pauses. The last pause repeats for as long as the
        // broker is down, so the consumer must never run past the end of the list.
        BrokerStub broker = new(true);
        Exception failure = new BrokerUnreachableException(new IOException("Connection refused"));
        IConnectionFactory connectionFactory = Substitute.For<IConnectionFactory>();
        connectionFactory.CreateConnectionAsync(Arg.Any<CancellationToken>())
            .Returns(
                Task.FromException<IConnection>(failure),
                Task.FromException<IConnection>(failure),
                Task.FromException<IConnection>(failure),
                Task.FromException<IConnection>(failure),
                Task.FromResult(broker.Connection));
        using AccountErasedConsumer consumer = new(
            connectionFactory,
            Substitute.For<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<AccountErasedConsumer>.Instance,
            [TimeSpan.Zero, TimeSpan.Zero],
            [TimeSpan.Zero],
            TimeSpan.FromMinutes(1));

        // Act
        await consumer.StartAsync(CancellationToken.None);
        bool attached = await broker.WaitUntilConsumingAsync();
        await StopWithinLimitAsync(consumer);

        // Assert
        attached.ShouldBeTrue();
    }

    /// <summary>
    /// The pause between connection attempts grows, and the last pause repeats for as long as the broker
    /// stays down.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhileTheBrokerStaysDown_ShouldWaitLongerBeforeEachAttemptUpToTheLastPause()
    {
        // Arrange
        TimeSpan[] expectedPauses =
        [
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(15),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1)
        ];
        BrokerStub broker = new(true);
        Exception failure = new BrokerUnreachableException(new IOException("Connection refused"));
        IConnectionFactory connectionFactory = Substitute.For<IConnectionFactory>();
        connectionFactory.CreateConnectionAsync(Arg.Any<CancellationToken>())
            .Returns(
                Task.FromException<IConnection>(failure),
                Task.FromException<IConnection>(failure),
                Task.FromException<IConnection>(failure),
                Task.FromException<IConnection>(failure),
                Task.FromResult(broker.Connection));
        using AccountErasedConsumer consumer = CreateConsumerOnTheFakeClock(connectionFactory, TimeSpan.FromMinutes(1));

        // Act
        await consumer.StartAsync(CancellationToken.None);
        IReadOnlyList<int> wakeUpsJustBeforeEachPauseEnds = await WaitThroughAsync(expectedPauses);
        bool attached = await broker.WaitUntilConsumingAsync();
        await StopWithinLimitAsync(consumer);

        // Assert: no pause ended early, and the attempt after the last pause attached
        wakeUpsJustBeforeEachPauseEnds.ShouldBe([0, 1, 2, 3]);
        attached.ShouldBeTrue();
    }

    /// <summary>
    /// The consumer looks once per watch interval, and a channel that is closed on two looks in a row
    /// makes it attach again.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenTheChannelStaysClosed_ShouldAttachAgainOnlyAfterTwoWatchIntervals()
    {
        // Arrange
        TimeSpan watchInterval = TimeSpan.FromSeconds(30);
        BrokerStub closedChannel = new(false);
        BrokerStub openChannel = new(true);
        IConnectionFactory connectionFactory = Substitute.For<IConnectionFactory>();
        connectionFactory.CreateConnectionAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(closedChannel.Connection), Task.FromResult(openChannel.Connection));
        using AccountErasedConsumer consumer = CreateConsumerOnTheFakeClock(connectionFactory, watchInterval);

        // Act
        await consumer.StartAsync(CancellationToken.None);
        IReadOnlyList<int> wakeUpsJustBeforeEachLook = await WaitThroughAsync([watchInterval, watchInterval]);
        bool attachedAgain = await openChannel.WaitUntilConsumingAsync();
        await StopWithinLimitAsync(consumer);

        // Assert: no look came early, and the second closed look attached again
        wakeUpsJustBeforeEachLook.ShouldBe([0, 1]);
        attachedAgain.ShouldBeTrue();
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
            TimeProvider.System,
            NullLogger<AccountErasedConsumer>.Instance,
            [TimeSpan.Zero],
            [TimeSpan.Zero],
            TimeSpan.FromMinutes(1));
    }

    private static AccountErasedConsumer CreateConsumer(IConnectionFactory connectionFactory, TimeSpan watchInterval) =>
        new(
            connectionFactory,
            Substitute.For<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<AccountErasedConsumer>.Instance,
            [TimeSpan.Zero],
            [TimeSpan.Zero],
            watchInterval);

    /// <summary>
    /// A consumer whose pauses after a failed erasure are the real ones, on the fake clock.
    /// </summary>
    private AccountErasedConsumer CreateConsumerOnTheFakeClock(StubEraseHandler handler, ILoggerFactory loggerFactory)
    {
        ServiceProvider services = new ServiceCollection()
            .AddScoped<ICommandHandler<EraseTranslatorProfile.Command, Result>>(_ => handler)
            .BuildServiceProvider();

        return new AccountErasedConsumer(
            Substitute.For<IConnectionFactory>(),
            services.GetRequiredService<IServiceScopeFactory>(),
            _clock,
            new Logger<AccountErasedConsumer>(loggerFactory),
            [TimeSpan.Zero],
            AccountErasedConsumer.RetryBackoffs,
            TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// A consumer whose pauses between connection attempts are the real ones, on the fake clock.
    /// </summary>
    private AccountErasedConsumer CreateConsumerOnTheFakeClock(IConnectionFactory connectionFactory, TimeSpan watchInterval) =>
        new(
            connectionFactory,
            Substitute.For<IServiceScopeFactory>(),
            _clock,
            NullLogger<AccountErasedConsumer>.Instance,
            AccountErasedConsumer.ConnectBackoffs,
            [TimeSpan.Zero],
            watchInterval);

    /// <summary>
    /// Hands the consumer one delivery per pause, lets each pause run out, and returns how many pauses
    /// had ended a millisecond before each one was due. A pause that is too long never lets its delivery
    /// finish, and the wait for it then fails the test.
    /// </summary>
    private async Task<IReadOnlyList<int>> DeliverThroughPausesAsync(
        AccountErasedConsumer consumer,
        IChannel channel,
        IEnumerable<TimeSpan> pauses)
    {
        List<int> wakeUpsJustBeforeEachPauseEnds = [];

        foreach (TimeSpan pause in pauses)
        {
            Task delivering = consumer.OnDeliveredAsync(channel, Delivery(), CancellationToken.None);
            await _clock.WaitForNextTimerAsync();
            _clock.Advance(pause - OneMillisecond);
            wakeUpsJustBeforeEachPauseEnds.Add(_clock.WakeUps);
            _clock.Advance(OneMillisecond);
            await delivering.WaitAsync(WaitLimit);
        }

        return wakeUpsJustBeforeEachPauseEnds;
    }

    /// <summary>
    /// Lets each pause or watch interval the consumer starts run out in turn, and returns how many had
    /// ended a millisecond before each one was due. One that is too long never starts the next one, and
    /// <see cref="TimerWatchingTimeProvider.WaitForNextTimerAsync"/> then fails the test.
    /// </summary>
    private async Task<IReadOnlyList<int>> WaitThroughAsync(IEnumerable<TimeSpan> pauses)
    {
        List<int> wakeUpsJustBeforeEachPauseEnds = [];

        foreach (TimeSpan pause in pauses)
        {
            await _clock.WaitForNextTimerAsync();
            _clock.Advance(pause - OneMillisecond);
            wakeUpsJustBeforeEachPauseEnds.Add(_clock.WakeUps);
            _clock.Advance(OneMillisecond);
        }

        return wakeUpsJustBeforeEachPauseEnds;
    }

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
