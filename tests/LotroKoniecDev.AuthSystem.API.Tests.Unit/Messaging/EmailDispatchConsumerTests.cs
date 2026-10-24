using System.Data;
using System.Data.Common;
using System.Text;
using LotroKoniecDev.AuthSystem.API.BackgroundServices;
using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Messaging;

/// <summary>
/// Pins what ADR-0036 promises only in prose: the backoff ladder and
/// <see cref="RabbitMqTopology.EmailDeliveryLimit"/> move together, and no pause outlives the broker's
/// consumer timeout (the consumer's <c>Math.Min</c> would hide any drift). Also pins the poison decision for
/// unusable ids (ADR-0037), attaching again (#1088), and, on a fake clock, which pause follows which failure
/// (#1223).
/// </summary>
public sealed class EmailDispatchConsumerTests
{
    private const ulong DeliveryTag = 42;
    private const string ConsumerTag = "email-consumer";
    private const string MessageType = "TestEmail";

    private const string DeliveryCountHeader = "x-delivery-count";

    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OneMillisecond = TimeSpan.FromMilliseconds(1);
    private static readonly Error SmtpRelayDown = new("Email.SendFailed", "The SMTP relay is down.");

    private static readonly DbContextOptions<AuthDbContext> EmptyInboxOptions =
        new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql("Host=email-dispatch-consumer-tests.invalid")
            .AddInterceptors(new EmptyInboxInterceptor())
            .Options;

    private readonly TimerWatchingTimeProvider _clock = new();

    [Fact]
    public void RedeliveryBackoffs_ComparedToDeliveryLimit_HasOneEntryPerAllowedRedelivery()
    {
        EmailDispatchConsumer.RedeliveryBackoffs.Length.ShouldBe(RabbitMqTopology.EmailDeliveryLimit);
    }

    [Fact]
    public void RedeliveryBackoffs_EveryPause_StaysUnderTheBrokerConsumerTimeout()
    {
        // The broker closes the channel when an ack takes longer than consumer_timeout (30 minutes by
        // default, compose does not change it). A longer pause would lose the channel and use up a retry.
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
        // Arrange
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

    /// <summary>
    /// The ladder outlasts an SMTP outage of about 30 minutes before the message goes to the dead-letter
    /// queue (ADR-0036).
    /// </summary>
    [Theory]
    [InlineData(0, 30)]
    [InlineData(1, 120)]
    [InlineData(2, 300)]
    [InlineData(3, 600)]
    [InlineData(4, 900)]
    public async Task OnDeliveredAsync_AfterATemporaryFailure_ShouldRequeueOnlyOnceThePauseForItsRetryCountRanOut(
        int redeliveries,
        int pauseSeconds)
    {
        // Arrange
        TimeSpan pause = TimeSpan.FromSeconds(pauseSeconds);
        IChannel channel = Substitute.For<IChannel>();
        await using ServiceProvider services = BuildDeliveryServices(
            new StubEmailProcessor(() => Result.Failure(SmtpRelayDown)));
        using EmailDispatchConsumer consumer = CreateConsumerOnTheFakeClock(services, NullLoggerFactory.Instance);

        // Act
        Task delivering = consumer.OnDeliveredAsync(channel, Delivery(redeliveries), CancellationToken.None);
        await _clock.WaitForNextTimerAsync();
        _clock.Advance(pause - OneMillisecond);
        int wakeUpsJustBeforeThePauseEnds = _clock.WakeUps;
        int channelCallsJustBeforeThePauseEnds = channel.ReceivedCalls().Count();
        _clock.Advance(OneMillisecond);
        await delivering.WaitAsync(WaitLimit);

        // Assert
        wakeUpsJustBeforeThePauseEnds.ShouldBe(0);
        channelCallsJustBeforeThePauseEnds.ShouldBe(0);
        await channel.Received(1).BasicRejectAsync(DeliveryTag, true, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// This reject moves the message to the dead-letter queue, so a pause before it would only hold the queue
    /// up.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    public async Task OnDeliveredAsync_AfterATemporaryFailureAtTheDeliveryLimit_ShouldRequeueWithoutAPause(
        int redeliveriesPastTheLimit)
    {
        // Arrange
        IChannel channel = Substitute.For<IChannel>();
        await using ServiceProvider services = BuildDeliveryServices(
            new StubEmailProcessor(() => Result.Failure(SmtpRelayDown)));
        using EmailDispatchConsumer consumer = CreateConsumerOnTheFakeClock(services, NullLoggerFactory.Instance);
        BasicDeliverEventArgs delivery = Delivery(RabbitMqTopology.EmailDeliveryLimit + redeliveriesPastTheLimit);

        // Act: the clock never moves, so any pause would keep the delivery from finishing
        await consumer.OnDeliveredAsync(channel, delivery, CancellationToken.None).WaitAsync(WaitLimit);

        // Assert
        await channel.Received(1).BasicRejectAsync(DeliveryTag, true, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// An unexpected error says nothing about the SMTP relay, so its pause stays at the first step.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task OnDeliveredAsync_AfterAnUnexpectedError_ShouldRequeueOnlyOnceTheFirstPauseRanOut(int redeliveries)
    {
        // Arrange
        TimeSpan firstPause = TimeSpan.FromSeconds(30);
        IChannel channel = Substitute.For<IChannel>();
        await using ServiceProvider services = BuildDeliveryServices(
            new StubEmailProcessor(() => throw new InvalidOperationException("A bug in the processor.")));
        using EmailDispatchConsumer consumer = CreateConsumerOnTheFakeClock(services, NullLoggerFactory.Instance);

        // Act
        Task delivering = consumer.OnDeliveredAsync(channel, Delivery(redeliveries), CancellationToken.None);
        await _clock.WaitForNextTimerAsync();
        _clock.Advance(firstPause - OneMillisecond);
        int wakeUpsJustBeforeThePauseEnds = _clock.WakeUps;
        int channelCallsJustBeforeThePauseEnds = channel.ReceivedCalls().Count();
        _clock.Advance(OneMillisecond);
        await delivering.WaitAsync(WaitLimit);

        // Assert
        wakeUpsJustBeforeThePauseEnds.ShouldBe(0);
        channelCallsJustBeforeThePauseEnds.ShouldBe(0);
        await channel.Received(1).BasicRejectAsync(DeliveryTag, true, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The broker puts an unacknowledged delivery back when the channel closes, and the next start handles it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnDeliveredAsync_WhenTheHostStopsDuringThePause_ShouldEndWithoutSettlingTheDelivery(
        bool theProcessorThrows)
    {
        // Arrange
        using CancellationTokenSource stopping = new();
        IChannel channel = Substitute.For<IChannel>();
        await using ServiceProvider services = BuildDeliveryServices(new StubEmailProcessor(() => theProcessorThrows
            ? throw new InvalidOperationException("A bug in the processor.")
            : Result.Failure(SmtpRelayDown)));
        using EmailDispatchConsumer consumer = CreateConsumerOnTheFakeClock(services, NullLoggerFactory.Instance);

        // Act
        Task delivering = consumer.OnDeliveredAsync(channel, Delivery(0), stopping.Token);
        await _clock.WaitForNextTimerAsync();
        await stopping.CancelAsync();
        await delivering.WaitAsync(WaitLimit);

        // Assert
        channel.ReceivedCalls().ShouldBeEmpty();
    }

    /// <summary>
    /// A stop is not a failure, so it adds no unexpected error to the log. The line written before the pause
    /// stays.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnDeliveredAsync_WhenTheHostStopsDuringThePause_ShouldLogNothingMore(bool theProcessorThrows)
    {
        // Arrange
        using CapturingLoggerFactory loggerFactory = new();
        using CancellationTokenSource stopping = new();
        IChannel channel = Substitute.For<IChannel>();
        await using ServiceProvider services = BuildDeliveryServices(new StubEmailProcessor(() => theProcessorThrows
            ? throw new InvalidOperationException("A bug in the processor.")
            : Result.Failure(SmtpRelayDown)));
        using EmailDispatchConsumer consumer = CreateConsumerOnTheFakeClock(services, loggerFactory);

        // Act
        Task delivering = consumer.OnDeliveredAsync(channel, Delivery(0), stopping.Token);
        await _clock.WaitForNextTimerAsync();
        int logEntriesBeforeTheStop = loggerFactory.Entries.Count;
        await stopping.CancelAsync();
        await delivering.WaitAsync(WaitLimit);

        // Assert
        loggerFactory.Entries.Count.ShouldBe(logEntriesBeforeTheStop);
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
        using EmailDispatchConsumer consumer = CreateConsumerOnTheFakeClock(connectionFactory, TimeSpan.FromMinutes(1));

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
    /// One closed look is left to automatic recovery; the second in a row makes the consumer attach again.
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
        using EmailDispatchConsumer consumer = CreateConsumerOnTheFakeClock(connectionFactory, EmailDispatchConsumer.WatchInterval);

        // Act: the timer for the second look starts only after the first look, and after any attach it
        // made, so the connection count read there is final.
        await consumer.StartAsync(CancellationToken.None);
        await _clock.WaitForNextTimerAsync();
        _clock.Advance(watchInterval - OneMillisecond);
        int wakeUpsJustBeforeTheFirstLook = _clock.WakeUps;
        _clock.Advance(OneMillisecond);
        await _clock.WaitForNextTimerAsync();
        int connectionsAfterTheFirstLook = connectionFactory.ReceivedCalls().Count();
        _clock.Advance(watchInterval);
        bool attachedAgain = await openChannel.WaitUntilConsumingAsync();
        await StopWithinLimitAsync(consumer);

        // Assert
        wakeUpsJustBeforeTheFirstLook.ShouldBe(0);
        connectionsAfterTheFirstLook.ShouldBe(1);
        attachedAgain.ShouldBeTrue();
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
        using EmailDispatchConsumer consumer = CreateConsumer(connectionFactory, TimeSpan.FromMilliseconds(10));

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
        IConnectionFactory connectionFactory = Substitute.For<IConnectionFactory>();
        connectionFactory.CreateConnectionAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(closedChannel.Connection), Task.FromResult(openChannel.Connection));
        using EmailDispatchConsumer consumer = CreateConsumer(connectionFactory, TimeSpan.FromMilliseconds(10));

        // Act
        await consumer.StartAsync(CancellationToken.None);
        await openChannel.WaitUntilConsumingAsync();
        await StopWithinLimitAsync(consumer);

        // Assert: resource cleanup, which the consumer returns nothing about
        await closedChannel.Connection.Received(1).DisposeAsync();
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

        // Act
        await consumer.StartAsync(CancellationToken.None);
        bool lookedThirtyTimes = await broker.WaitForLooksAsync(30);
        await StopWithinLimitAsync(consumer);

        // Assert: a new connection is a side effect the consumer returns nothing about
        lookedThirtyTimes.ShouldBeTrue();
        await connectionFactory.Received(1).CreateConnectionAsync(Arg.Any<CancellationToken>());
    }

    private static EmailDispatchConsumer CreateConsumer(IConnectionFactory connectionFactory, TimeSpan watchInterval) =>
        new(
            connectionFactory,
            Substitute.For<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<EmailDispatchConsumer>.Instance,
            [TimeSpan.Zero],
            watchInterval);

    private EmailDispatchConsumer CreateConsumerOnTheFakeClock(IServiceProvider services, ILoggerFactory loggerFactory) =>
        new(
            Substitute.For<IConnectionFactory>(),
            services.GetRequiredService<IServiceScopeFactory>(),
            _clock,
            new Logger<EmailDispatchConsumer>(loggerFactory),
            [TimeSpan.Zero],
            TimeSpan.FromMinutes(1));

    private EmailDispatchConsumer CreateConsumerOnTheFakeClock(IConnectionFactory connectionFactory, TimeSpan watchInterval) =>
        new(
            connectionFactory,
            Substitute.For<IServiceScopeFactory>(),
            _clock,
            NullLogger<EmailDispatchConsumer>.Instance,
            EmailDispatchConsumer.ConnectBackoffs,
            watchInterval);

    /// <summary>
    /// The processor for <see cref="MessageType"/> and the inbox wrapper, over an inbox that has not seen the
    /// message.
    /// </summary>
    private static ServiceProvider BuildDeliveryServices(StubEmailProcessor processor)
    {
        ServiceCollection services = new();
        services.AddKeyedSingleton<IEmailMessageProcessor>(MessageType, processor);
        services.AddScoped<AuthDbContext>(_ => new AuthDbContext(EmptyInboxOptions));
        services.AddScoped<EmailDeliveryProcessor>(serviceProvider => new EmailDeliveryProcessor(
            serviceProvider.GetRequiredService<AuthDbContext>(),
            TimeProvider.System,
            NullLogger<EmailDeliveryProcessor>.Instance));
        return services.BuildServiceProvider();
    }

    private static BasicDeliverEventArgs Delivery(int redeliveries)
    {
        BasicProperties properties = new()
        {
            MessageId = Guid.CreateVersion7().ToString(),
            Type = MessageType,
            Headers = new Dictionary<string, object?> { [DeliveryCountHeader] = (long)redeliveries }
        };

        return new BasicDeliverEventArgs(
            consumerTag: ConsumerTag,
            deliveryTag: DeliveryTag,
            redelivered: redeliveries > 0,
            exchange: RabbitMqTopology.EmailsExchange,
            routingKey: RabbitMqTopology.EmailConfirmationRoutingKey,
            properties: properties,
            body: Encoding.UTF8.GetBytes("{}"));
    }

    /// <summary>
    /// Returns how many pauses had ended a millisecond before each one was due. A pause that is too long never
    /// starts the next, and <see cref="TimerWatchingTimeProvider.WaitForNextTimerAsync"/> fails the test.
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
    /// Stops the consumer but waits at most <see cref="WaitLimit"/>, so a consumer that ignores its stop
    /// token cannot hang the run. It returns quietly when the limit runs out, so the test about stopping
    /// checks the outcome itself.
    /// </summary>
    private static async Task StopWithinLimitAsync(EmailDispatchConsumer consumer)
    {
        using CancellationTokenSource stopLimit = new(WaitLimit);
        await consumer.StopAsync(stopLimit.Token);
    }

    /// <summary>
    /// Answers every send with the result or the exception the test set.
    /// </summary>
    private sealed class StubEmailProcessor : IEmailMessageProcessor
    {
        private readonly Func<Result> _answer;

        public object? TryDeserialize(ReadOnlySpan<byte> body) => new object();

        public Task<Result> ProcessAsync(object message, CancellationToken cancellationToken) =>
            Task.FromResult(_answer());

        public StubEmailProcessor(Func<Result> answer)
        {
            _answer = answer;
        }
    }

    /// <summary>
    /// An inbox with no rows. It opens no connection and answers the duplicate check with "not seen".
    /// </summary>
    private sealed class EmptyInboxInterceptor : DbCommandInterceptor, IDbConnectionInterceptor
    {
        public ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(InterceptionResult.Suppress());

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            DataTable answer = new();
            answer.Columns.Add("exists", typeof(bool));
            answer.Rows.Add(false);
            return ValueTask.FromResult(InterceptionResult<DbDataReader>.SuppressWithResult(answer.CreateDataReader()));
        }
    }

    /// <summary>
    /// Stands in for the broker client. The channel confirms a subscription as the broker does, so a test can
    /// wait for the consumer, cancel the subscription and count its looks.
    /// </summary>
    private sealed class BrokerStub
    {
        private readonly bool[] _channelOpenOnLooks;
        private readonly TaskCompletionSource _consuming = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IAsyncBasicConsumer? _consumer;
        private int _looks;

        public IConnection Connection { get; }

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
        /// One answer per look to "is the channel open"; the last answer holds for every later look.
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
        }

        private bool AnswerNextLook()
        {
            int look = Interlocked.Increment(ref _looks);
            return _channelOpenOnLooks[Math.Min(look, _channelOpenOnLooks.Length) - 1];
        }
    }
}
