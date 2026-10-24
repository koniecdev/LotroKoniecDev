using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Monads;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace LotroKoniecDev.AuthSystem.API.BackgroundServices;

/// <summary>
/// The one e-mail consumer (ADR-0038). It picks the <see cref="IEmailMessageProcessor"/> by the AMQP
/// <c>type</c> property, never by the routing key: the type says what the payload is, the routing key
/// only which bindings receive it (see <c>OutboxMessageRouting</c>).
/// </summary>
/// <remarks>
/// Manual ack, only after the processor finished, so a crash puts the delivery back (at-least-once); the
/// inbox then drops the duplicate (ADR-0037).
/// Failures go three ways (ADR-0036): a payload that can never be handled goes straight to the
/// dead-letter queue, a temporary failure is requeued after a growing pause, and the broker parks a
/// message past <see cref="RabbitMqTopology.EmailDeliveryLimit"/>.
/// Always <c>basic.reject</c>, never <c>basic.nack</c>: since RabbitMQ 4.3 a nack with requeue is redelivered
/// forever without raising <c>x-delivery-count</c>.
/// A broker that is down must not stop the app from starting, so it connects with a backoff. Automatic
/// recovery does not bring back a closed channel or a cancelled subscription, so the consumer checks it is
/// still attached and attaches again (#1088), as the TMS account event consumer does (ADR-0065 §4).
/// </remarks>
internal sealed partial class EmailDispatchConsumer : BackgroundService
{
    internal const string ClientProvidedName = "lotro-auth-api-email-consumer";

    /// <summary>
    /// The top stays low: the broker is a container on the same machine, an outage lasts about as long as
    /// a deploy, and a retry costs only a local TCP connect.
    /// </summary>
    internal static readonly TimeSpan[] ConnectBackoffs =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromMinutes(1)
    ];

    /// <summary>
    /// One entry per retry <see cref="RabbitMqTopology.EmailDeliveryLimit"/> allows, so change them
    /// together. Without a pause the broker redelivers at once and a down SMTP relay would use up the
    /// limit in seconds. The whole ladder outlasts a realistic 30-minute SMTP outage. Blocking the
    /// consumer is fine, because every message needs the same relay. Each entry must stay well below the
    /// broker's 30-minute consumer timeout, because the pause holds the delivery unacknowledged. The unit
    /// tests check both rules.
    /// </summary>
    internal static readonly TimeSpan[] RedeliveryBackoffs =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(15)
    ];

    /// <summary>
    /// How often the consumer checks that it is still attached.
    /// </summary>
    internal static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(30);

    private readonly IConnectionFactory _connectionFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EmailDispatchConsumer> _logger;
    private readonly TimeSpan[] _connectBackoffs;
    private readonly TimeSpan _watchInterval;

    private IConnection? _connection;
    private IChannel? _channel;

    /// <summary>
    /// The broker message id the inbox uses to spot duplicates (ADR-0037).
    /// </summary>
    internal static bool TryReadMessageId(IReadOnlyBasicProperties properties, out Guid messageId)
    {
        return Guid.TryParse(properties.MessageId, out messageId) && messageId != Guid.Empty;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                AsyncEventingBasicConsumer consumer = await AttachConsumerWithRetryAsync(stoppingToken);
                LogStarted(_logger, RabbitMqTopology.EmailQueue);

                // The work happens in OnDeliveredAsync on the client library's loop; this only watches.
                await WaitWhileAttachedAsync(consumer, stoppingToken);

                LogDetached(_logger, RabbitMqTopology.EmailQueue);
                await CloseAsync();
            }
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            // The app is shutting down, which is not an error, whatever the attach in flight threw.
        }
        finally
        {
            await CloseAsync();
        }
    }

    /// <summary>
    /// Every path must end in exactly one ack or reject: the client library swallows an exception, and the
    /// delivery then stays stuck until the channel dies.
    /// </summary>
    internal async Task OnDeliveredAsync(
        IChannel channel,
        BasicDeliverEventArgs delivery,
        CancellationToken stoppingToken)
    {
        try
        {
            if (!TryReadMessageId(delivery.BasicProperties, out Guid messageId))
            {
                // Without a usable id the inbox cannot spot a duplicate (ADR-0037), so it waits in the
                // dead-letter queue for a person.
                LogMessageIdUnusable(_logger, delivery.BasicProperties.MessageId);
                await channel.BasicRejectAsync(
                    delivery.DeliveryTag,
                    requeue: false,
                    cancellationToken: stoppingToken);
                return;
            }

            Result ackDecision;
            await using (AsyncServiceScope scope = _scopeFactory.CreateAsyncScope())
            {
                string? messageType = delivery.BasicProperties.Type;
                IEmailMessageProcessor? processor = string.IsNullOrWhiteSpace(messageType)
                    ? null
                    : scope.ServiceProvider.GetKeyedService<IEmailMessageProcessor>(messageType);
                if (processor is null)
                {
                    // No processor for a missing or unknown type, and a retry would not change that
                    // (ADR-0038).
                    LogUnknownMessageType(_logger, delivery.BasicProperties.MessageId, messageType);
                    await channel.BasicRejectAsync(
                        delivery.DeliveryTag,
                        requeue: false,
                        cancellationToken: stoppingToken);
                    return;
                }

                object? message = processor.TryDeserialize(delivery.Body.Span);
                if (message is null)
                {
                    // An unreadable payload stays unreadable, so it goes to the dead-letter queue at once.
                    LogPoisonMessage(_logger, delivery.BasicProperties.MessageId);
                    await channel.BasicRejectAsync(
                        delivery.DeliveryTag,
                        requeue: false,
                        cancellationToken: stoppingToken);
                    return;
                }

                EmailDeliveryProcessor deliveryProcessor =
                    scope.ServiceProvider.GetRequiredService<EmailDeliveryProcessor>();
                ackDecision = await deliveryProcessor.ProcessOnceAsync(
                    processor, message, messageId, stoppingToken);
            }

            if (ackDecision.IsSuccess)
            {
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
                return;
            }

            int redeliveries = RedeliveryCount.Read(delivery.BasicProperties.Headers);
            if (redeliveries >= RabbitMqTopology.EmailDeliveryLimit)
            {
                // The last attempt: this reject takes the count past the limit, so the broker
                // dead-letters it, and no pause is needed.
                LogRetriesExhausted(
                    _logger,
                    delivery.BasicProperties.MessageId,
                    ackDecision.Error,
                    RabbitMqTopology.EmailDeliveryLimit);
                await channel.BasicRejectAsync(
                    delivery.DeliveryTag,
                    requeue: true,
                    cancellationToken: stoppingToken);
                return;
            }

            LogTransientFailure(
                _logger,
                delivery.BasicProperties.MessageId,
                ackDecision.Error,
                redeliveries + 1,
                RabbitMqTopology.EmailDeliveryLimit);
            await Task.Delay(
                RedeliveryBackoffs[Math.Min(redeliveries, RedeliveryBackoffs.Length - 1)],
                _timeProvider,
                stoppingToken);
            await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: true, cancellationToken: stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Neither ack nor reject: closing the channel puts the delivery back for the next start.
        }
        catch (Exception ex)
        {
            LogUnexpectedError(_logger, ex, delivery.BasicProperties.MessageId);
            await TryRequeueAsync(channel, delivery.DeliveryTag, stoppingToken);
        }
    }

    public EmailDispatchConsumer(
        IOptions<RabbitMqOptions> options,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<EmailDispatchConsumer> logger)
        : this(
            RabbitMqConnectionFactoryBuilder.Build(options.Value, ClientProvidedName),
            scopeFactory,
            timeProvider,
            logger,
            ConnectBackoffs,
            WatchInterval)
    {
    }

    /// <summary>
    /// For tests: their own broker client and shorter pauses. Every pause runs on
    /// <paramref name="timeProvider"/>, so a fake clock can drive them (#1223).
    /// </summary>
    internal EmailDispatchConsumer(
        IConnectionFactory connectionFactory,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<EmailDispatchConsumer> logger,
        TimeSpan[] connectBackoffs,
        TimeSpan watchInterval)
    {
        ArgumentOutOfRangeException.ThrowIfZero(connectBackoffs.Length);
        ArgumentOutOfRangeException.ThrowIfLessThan(connectBackoffs.Min(), TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(watchInterval, TimeSpan.Zero);

        _connectionFactory = connectionFactory;
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
        _connectBackoffs = connectBackoffs;
        _watchInterval = watchInterval;
    }

    /// <summary>
    /// The whole attach is one retried attempt: an exception leaving <see cref="ExecuteAsync"/> would stop
    /// the host (<see cref="BackgroundServiceExceptionBehavior.StopHost"/>), and a broken e-mail path must
    /// never take login down. Every failure is retried, a client timeout included; only the host's stop
    /// token ends the loop (#1088). A failed attempt disposes what it opened, or automatic recovery would
    /// keep half-attached connections alive forever.
    /// </summary>
    private async Task<AsyncEventingBasicConsumer> AttachConsumerWithRetryAsync(CancellationToken stoppingToken)
    {
        int failedAttempts = 0;

        while (true)
        {
            try
            {
                _connection = await _connectionFactory.CreateConnectionAsync(stoppingToken);
                IChannel channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);
                _channel = channel;
                await RabbitMqTopologyDeclaration.DeclareAsync(channel, stoppingToken);

                // Prefetch 1, so a backlog never floods this process and e-mails go out one by one.
                await channel.BasicQosAsync(
                    prefetchSize: 0,
                    prefetchCount: 1,
                    global: false,
                    cancellationToken: stoppingToken);

                AsyncEventingBasicConsumer consumer = new(channel);
                consumer.ReceivedAsync += (_, delivery) => OnDeliveredAsync(channel, delivery, stoppingToken);

                await channel.BasicConsumeAsync(
                    queue: RabbitMqTopology.EmailQueue,
                    autoAck: false,
                    consumer: consumer,
                    cancellationToken: stoppingToken);

                return consumer;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                await CloseAsync();
                failedAttempts++;
                TimeSpan wait = _connectBackoffs[Math.Min(failedAttempts - 1, _connectBackoffs.Length - 1)];
                LogConnectFailed(_logger, ex, wait.TotalSeconds);
                await Task.Delay(wait, _timeProvider, stoppingToken);
            }
        }
    }

    /// <summary>
    /// Two detached looks in a row, so automatic recovery gets its chance after a lost connection.
    /// </summary>
    private async Task WaitWhileAttachedAsync(AsyncEventingBasicConsumer consumer, CancellationToken stoppingToken)
    {
        int detachedLooks = 0;

        while (detachedLooks < 2)
        {
            await Task.Delay(_watchInterval, _timeProvider, stoppingToken);

            bool attached = _channel is { IsOpen: true } && consumer.IsRunning;
            detachedLooks = attached ? 0 : detachedLooks + 1;
        }
    }

    /// <summary>
    /// If the reject fails, the broker puts the delivery back when the channel closes, and that also
    /// raises the delivery count, so even a crash loop ends at the limit. The pause is always the first
    /// step: an unexpected exception says nothing about the SMTP relay, so it only slows a hot loop.
    /// </summary>
    private async Task TryRequeueAsync(IChannel channel, ulong deliveryTag, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(RedeliveryBackoffs[0], _timeProvider, stoppingToken);
            await channel.BasicRejectAsync(deliveryTag, requeue: true, cancellationToken: stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Shutting down: the delivery goes back when the channel closes.
        }
        catch (Exception ex)
        {
            LogUnexpectedError(_logger, ex, messageId: null);
        }
    }

    private async Task CloseAsync()
    {
        IChannel? channel = _channel;
        _channel = null;

        if (channel is not null)
        {
            try
            {
                await channel.DisposeAsync();
            }
            catch (Exception ex)
            {
                LogTeardownWarning(_logger, ex);
            }
        }

        IConnection? connection = _connection;
        _connection = null;

        if (connection is not null)
        {
            try
            {
                await connection.DisposeAsync();
            }
            catch (Exception ex)
            {
                LogTeardownWarning(_logger, ex);
            }
        }
    }

    [LoggerMessage(
        EventId = EventIds.EmailConsumerStarted,
        Level = LogLevel.Information,
        Message = "Consuming e-mail messages from queue {Queue}")]
    private static partial void LogStarted(ILogger logger, string queue);

    [LoggerMessage(
        EventId = EventIds.EmailConsumerDetached,
        Level = LogLevel.Warning,
        Message = "The e-mail consumer is no longer attached to queue {Queue}: its channel is closed or its subscription is gone. Attaching again.")]
    private static partial void LogDetached(ILogger logger, string queue);

    [LoggerMessage(
        EventId = EventIds.EmailConsumerConnectFailed,
        Level = LogLevel.Warning,
        Message = "Connecting the e-mail consumer to the broker failed; retrying in {DelaySeconds}s")]
    private static partial void LogConnectFailed(ILogger logger, Exception exception, double delaySeconds);

    [LoggerMessage(
        EventId = EventIds.EmailConsumerPoisonMessage,
        Level = LogLevel.Error,
        Message = "Rejecting poison message {MessageId} into the dead-letter queue: the payload could not be read as a known contract")]
    private static partial void LogPoisonMessage(ILogger logger, string? messageId);

    [LoggerMessage(
        EventId = EventIds.EmailConsumerMessageIdUnusable,
        Level = LogLevel.Error,
        Message = "Rejecting message with unusable message id {MessageId} into the dead-letter queue: the inbox cannot deduplicate a delivery without an id")]
    private static partial void LogMessageIdUnusable(ILogger logger, string? messageId);

    [LoggerMessage(
        EventId = EventIds.EmailConsumerUnknownMessageType,
        Level = LogLevel.Error,
        Message = "Rejecting message {MessageId} into the dead-letter queue: no processor is registered for message type {MessageType}")]
    private static partial void LogUnknownMessageType(ILogger logger, string? messageId, string? messageType);

    [LoggerMessage(
        EventId = EventIds.EmailConsumerTransientFailure,
        Level = LogLevel.Warning,
        Message = "Processing message {MessageId} failed with {Error}; requeueing for redelivery {Redelivery} of {DeliveryLimit}")]
    private static partial void LogTransientFailure(ILogger logger, string? messageId, Error error, int redelivery, int deliveryLimit);

    [LoggerMessage(
        EventId = EventIds.EmailConsumerRetriesExhausted,
        Level = LogLevel.Error,
        Message = "Processing message {MessageId} still failed with {Error} after {DeliveryLimit} redeliveries; the broker moves it to the dead-letter queue")]
    private static partial void LogRetriesExhausted(ILogger logger, string? messageId, Error error, int deliveryLimit);

    [LoggerMessage(
        EventId = EventIds.EmailConsumerUnexpectedError,
        Level = LogLevel.Error,
        Message = "Unexpected error while handling message {MessageId}")]
    private static partial void LogUnexpectedError(ILogger logger, Exception exception, string? messageId);

    [LoggerMessage(
        EventId = EventIds.EmailConsumerTeardownWarning,
        Level = LogLevel.Debug,
        Message = "Failed to cleanly close the consumer connection or channel")]
    private static partial void LogTeardownWarning(ILogger logger, Exception exception);
}
